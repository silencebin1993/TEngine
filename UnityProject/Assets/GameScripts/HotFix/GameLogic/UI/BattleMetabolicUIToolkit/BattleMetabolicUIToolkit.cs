using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.Battle;

namespace GameLogic
{
    /// <summary>
    /// UI Toolkit 版代谢切片装配面板（battle-ui-toolkit/story-002）。储备囊/基因条/3×3 切片/
    /// 画边删边/囊满溢出抉择，唯一数据源是 <see cref="MetabolicSlicePanel.Instance"/>，本控制器
    /// 不持有任何装/卸/画边状态，只做 UI Toolkit 树查询+事件转发+每帧刷新显示（D8）。
    /// 不接 [Window]/CellStageFlow._hub，照抄 <see cref="BattleHudToolkit"/> 的
    /// "常驻单例、轮询 IsRunning 自控显隐"模式（D2）。M 键默认打开本面板；旧 IMGUI 3×3 装配
    /// 面板改绑 L 键作对照入口（D10）。
    /// </summary>
    public class BattleMetabolicUIToolkit : MonoBehaviour
    {
        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _bagItemTemplate;
        private VisualTreeAsset _tagChipTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _overflowBanner;
        private Label _overflowText;
        private Button _btnDiscardNew;
        private Button _btnDiscardOldKeepNew;
        private Label _bagTitle;
        private ScrollView _bagList;
        private VisualElement _geneStrip;
        private readonly Button[] _slotButtons = new Button[SlotGrid.SlotCount];
        private Button _btnAddEdge;
        private Button _btnRemoveEdge;
        private Label _edgeList;

        private bool _panelVisible;

        /// <summary>供 execute_code 验收探针只读访问。</summary>
        public bool IsPanelVisible => _panelVisible;

        /// <summary>story-005：供 BattleOverlayUIToolkit（Pause 面板"打开代谢"按钮）跨控制器调用，最小暴露，不新建平行显隐状态。</summary>
        public static BattleMetabolicUIToolkit Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BattleMetabolicUI");
            _bagItemTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BagItem");
            _tagChipTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("TagChip");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");

            if (this == null)
            {
                // 组件在异步加载期间被销毁（例如热更域重载）。
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;

            _root = _document.rootVisualElement;
            CacheNodes();
            SetVisible(false);
        }

        private void CacheNodes()
        {
            _overflowBanner = _root.Q<VisualElement>("OverflowBanner");
            _overflowText = _root.Q<Label>("OverflowText");
            _btnDiscardNew = _root.Q<Button>("BtnDiscardNew");
            _btnDiscardOldKeepNew = _root.Q<Button>("BtnDiscardOldKeepNew");
            _bagTitle = _root.Q<Label>("BagTitle");
            _bagList = _root.Q<ScrollView>("BagList");
            _geneStrip = _root.Q<VisualElement>("GeneStrip");
            _btnAddEdge = _root.Q<Button>("BtnAddEdge");
            _btnRemoveEdge = _root.Q<Button>("BtnRemoveEdge");
            _edgeList = _root.Q<Label>("EdgeList");

            for (int i = 0; i < SlotGrid.SlotCount; i++)
            {
                int slotId = i;
                // <ui:Instance name="SlotN"> 是 TemplateContainer，真正的 Button 在其内部按
                // SlotCell.uxml 命名为 "SlotCell"（同 story-001 SkillSlot 手法：先取容器再取内部节点）。
                VisualElement container = _root.Q<VisualElement>("Slot" + i);
                Button slot = container?.Q<Button>("SlotCell");
                _slotButtons[i] = slot;
                if (slot != null)
                {
                    slot.clicked += () => MetabolicSlicePanel.Instance?.HandleSlotClick(slotId);
                }
            }

            if (_btnAddEdge != null)
            {
                _btnAddEdge.clicked += () => MetabolicSlicePanel.Instance?.ToggleEdgeAddMode();
            }
            if (_btnRemoveEdge != null)
            {
                _btnRemoveEdge.clicked += () => MetabolicSlicePanel.Instance?.ToggleEdgeRemoveMode();
            }
            if (_btnDiscardNew != null)
            {
                _btnDiscardNew.clicked += () => MetabolicSlicePanel.Instance?.ResolveOverflowDiscardNew();
            }
            if (_btnDiscardOldKeepNew != null)
            {
                _btnDiscardOldKeepNew.clicked += () => MetabolicSlicePanel.Instance?.ResolveOverflowKeepNewDiscardOld();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.M))
            {
                SetVisible(!_panelVisible);
            }

            if (_root == null)
            {
                return;
            }

            CellStageFlow cell = GameRoot.CellStage;
            bool running = cell != null && cell.IsRunning;
            bool visible = running && _panelVisible;
            _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible)
            {
                return;
            }

            RefreshPanel();
        }

        public void SetVisible(bool visible)
        {
            _panelVisible = visible;
        }

        /// <summary>唯一数据源 MetabolicSlicePanel.Instance；本方法只做只读展示同步，不驱动任何状态变化（D8）。</summary>
        private void RefreshPanel()
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }

            RefreshOverflowBanner(panel);
            RefreshBagList(panel);
            RefreshGeneStrip(panel);
            RefreshSliceGrid(panel);
            RefreshEdgeToolbar(panel);
        }

        /// <summary>囊满溢出横幅（D12）：显隐 = PendingOverflow != null；文案动态改写。</summary>
        private void RefreshOverflowBanner(MetabolicSlicePanel panel)
        {
            PartInstance overflow = panel.PendingOverflow;
            bool hasOverflow = overflow != null;
            if (_overflowBanner != null)
            {
                _overflowBanner.style.display = hasOverflow ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (hasOverflow && _overflowText != null)
            {
                _overflowText.text = $"储备囊已满，新元素 {panel.DisplayName(overflow.CardDefId)} 待抉择";
            }
        }

        /// <summary>BagList 变长列表（D6）：每次刷新清空重建，成本可忽略（上限 8）。</summary>
        private void RefreshBagList(MetabolicSlicePanel panel)
        {
            if (_bagList == null)
            {
                return;
            }

            IReadOnlyList<PartInstance> items = panel.Bag.Items;
            if (_bagTitle != null)
            {
                _bagTitle.text = $"储备囊 {items.Count}/{panel.Bag.Cap}";
            }

            _bagList.Clear();
            foreach (PartInstance item in items)
            {
                if (_bagItemTemplate == null)
                {
                    continue;
                }
                TemplateContainer clone = _bagItemTemplate.CloneTree();
                Button button = clone.Q<Button>("BagItem");
                if (button == null)
                {
                    continue;
                }
                button.text = panel.DisplayName(item.CardDefId);
                button.RemoveFromClassList("selected");
                if (item.PartId == panel.SelectedPartId)
                {
                    button.AddToClassList("selected");
                }
                string partId = item.PartId;
                button.clicked += () => MetabolicSlicePanel.Instance?.ToggleSelectPart(partId);
                _bagList.Add(clone);
            }
        }

        /// <summary>GeneStrip（D7）：复用 story-001 已拷贝的 TagChip 模板，清空重建。</summary>
        private void RefreshGeneStrip(MetabolicSlicePanel panel)
        {
            if (_geneStrip == null)
            {
                return;
            }

            _geneStrip.Clear();
            IReadOnlyList<string> geneIds = panel.GeneIds;
            for (int i = 0; i < geneIds.Count; i++)
            {
                if (_tagChipTemplate == null)
                {
                    continue;
                }
                TemplateContainer clone = _tagChipTemplate.CloneTree();
                Label label = clone.Q<Label>("TagChip");
                if (label != null)
                {
                    label.text = GeneCatalog.GetDisplayName(geneIds[i]) ?? geneIds[i];
                }
                _geneStrip.Add(clone);
            }
        }

        /// <summary>SliceGrid 固定 9 格（D7）：直接更新已缓存 Button 的文本+class，不重建节点。</summary>
        private void RefreshSliceGrid(MetabolicSlicePanel panel)
        {
            SlotGrid grid = panel.Grid;
            int? edgeFrom = panel.EdgeFromSlot;

            for (int i = 0; i < SlotGrid.SlotCount; i++)
            {
                Button slot = _slotButtons[i];
                if (slot == null)
                {
                    continue;
                }

                SlotNode node = grid.Slots[i];
                bool isFrom = edgeFrom == i;
                slot.text = node.IsEmpty ? $"[{i}] 空" : $"[{i}] {panel.DisplayName(node.Part.CardDefId)}";

                slot.RemoveFromClassList("slot-empty");
                slot.RemoveFromClassList("slot-filled");
                slot.RemoveFromClassList("slot-from");
                slot.AddToClassList(node.IsEmpty ? "slot-empty" : "slot-filled");
                if (isFrom)
                {
                    slot.AddToClassList("slot-from");
                }
            }
        }

        /// <summary>画边/删边模式按钮 class + 箭头文本列表（D7/D12 同源）。</summary>
        private void RefreshEdgeToolbar(MetabolicSlicePanel panel)
        {
            if (_btnAddEdge != null)
            {
                _btnAddEdge.RemoveFromClassList("mode-active");
                if (panel.IsEdgeAddMode)
                {
                    _btnAddEdge.AddToClassList("mode-active");
                }
            }
            if (_btnRemoveEdge != null)
            {
                _btnRemoveEdge.RemoveFromClassList("mode-active");
                if (panel.IsEdgeRemoveMode)
                {
                    _btnRemoveEdge.AddToClassList("mode-active");
                }
            }

            if (_edgeList == null)
            {
                return;
            }
            IReadOnlyList<DirectedEdge> edges = panel.Grid.Edges;
            if (edges.Count == 0)
            {
                _edgeList.text = "箭头：（无）";
                return;
            }
            var texts = new List<string>(edges.Count);
            foreach (DirectedEdge e in edges)
            {
                texts.Add($"{e.From}→{e.To}");
            }
            _edgeList.text = "箭头：" + string.Join("　", texts);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_bagItemTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_bagItemTemplate);
                _bagItemTemplate = null;
            }
            if (_tagChipTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_tagChipTemplate);
                _tagChipTemplate = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
