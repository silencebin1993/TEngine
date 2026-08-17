using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.CardDefs;
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

        // ---- story-004：真拖拽（装/卸/画边），见 preflight-decisions.md D1~D6 ----
        private const float DragThreshold = 6f;

        private enum DragSourceKind { None, Bag, Slot }
        private enum DropState { None, Valid, Invalid }

        private Label _dragGhost;
        private DragSourceKind _dragSourceKind = DragSourceKind.None;
        private string _dragPartId;
        private int _dragFromSlot = -1;
        private bool _dragOriginEmpty;
        private bool _dragActive;
        private bool _dragModeLocked;
        private int _dragPointerId = -1;
        private VisualElement _dragCaptureElement;
        private VisualElement _lastHighlighted;
        private Vector2 _dragStartPos;

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
            // 多个 UIDocument 共用同一份 PanelSettings 时，兄弟节点绘制顺序取决于
            // 各自异步加载完成的竞态顺序（非确定性）。显式给一个数值表锁定的
            // sortingOrder，让四个控制器的叠放关系确定（story-004 已验证的手法）。
            _document.sortingOrder = 3;

            // UIDocument.rootVisualElement 在刚赋值 panelSettings 后偶发仍为 null
            // （面板尚未在本帧完成挂载，实测复现），有限帧数轮询等它就绪，避免
            // CacheNodes() 对 null 根节点查询直接崩溃、控制器永久半初始化。
            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[BattleMetabolicUIToolkit] rootVisualElement 等待超时，代谢面板未初始化。");
                return;
            }
            CacheNodes();
            CreateDragGhost();
            SetVisible(false);
        }

        /// <summary>D4：常驻拖影 Label，PickingMode.Ignore 防止自己挡住命中测试。</summary>
        private void CreateDragGhost()
        {
            _dragGhost = new Label();
            _dragGhost.AddToClassList("drag-ghost");
            _dragGhost.pickingMode = PickingMode.Ignore;
            _dragGhost.style.position = Position.Absolute;
            _dragGhost.style.display = DisplayStyle.None;
            _root.Add(_dragGhost);
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
                    // story-004 D1：旧 clicked 改成指针事件驱动的拖拽手势；点击语义在
                    // PointerUp 内按阈值分支手动调用（HandleDragEnd），不再用 clicked。
                    slot.RegisterCallback<PointerDownEvent>(evt => OnSlotPointerDown(evt, slot, slotId));
                    slot.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                    slot.RegisterCallback<PointerUpEvent>(OnPointerUp);
                    slot.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
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

            if (_dragSourceKind == DragSourceKind.Bag)
            {
                // story-004：拖拽起点是某个 BagItem 按钮本身，若本帧照常清空重建列表会销毁
                // 正在 CapturePointer 的元素，UIElements 会把这当成意外丢失捕获触发
                // PointerCaptureOutEvent，导致拖拽还没开始移动就被打断。装/卸/画边不会在
                // 拖拽过程中改 Bag/Grid（只在 PointerUp 落点时一次性调用），冻结列表安全。
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
                // story-004 D1：旧 clicked 改成指针事件驱动的拖拽手势，理由同 CacheNodes() 的 SlotCell。
                button.RegisterCallback<PointerDownEvent>(evt => OnBagPointerDown(evt, button, partId));
                button.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                button.RegisterCallback<PointerUpEvent>(OnPointerUp);
                button.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
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

        // ==== story-004：真拖拽手势（指针事件，见 preflight-decisions.md D1~D6） ====

        private void OnSlotPointerDown(PointerDownEvent evt, Button slot, int slotId)
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }
            bool originEmpty = panel.Grid.Slots[slotId].IsEmpty;
            BeginDrag(DragSourceKind.Slot, slotId, null, originEmpty, panel, slot, evt);
        }

        private void OnBagPointerDown(PointerDownEvent evt, Button button, string partId)
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }
            BeginDrag(DragSourceKind.Bag, -1, partId, false, panel, button, evt);
        }

        private void BeginDrag(DragSourceKind kind, int slotId, string partId, bool originEmpty, MetabolicSlicePanel panel, VisualElement element, PointerDownEvent evt)
        {
            _dragSourceKind = kind;
            _dragFromSlot = slotId;
            _dragPartId = partId;
            _dragOriginEmpty = originEmpty;
            _dragStartPos = evt.position;
            _dragActive = false;
            _dragModeLocked = panel.IsEdgeAddMode || panel.IsEdgeRemoveMode;
            _dragPointerId = evt.pointerId;
            _dragCaptureElement = element;
            element.CapturePointer(evt.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_dragSourceKind == DragSourceKind.None || _dragModeLocked)
            {
                return;
            }

            Vector2 pos = evt.position;
            if (!_dragActive)
            {
                if (Vector2.Distance(pos, _dragStartPos) <= DragThreshold)
                {
                    return;
                }
                _dragActive = true;
            }

            if (_dragSourceKind == DragSourceKind.Slot && _dragOriginEmpty)
            {
                // D3②：起点空槽，越过阈值也是无操作占位，不显示拖影/高亮。
                return;
            }

            UpdateGhost(pos);
            UpdateHighlight(pos);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_dragSourceKind == DragSourceKind.None)
            {
                return;
            }
            HandleDragEnd(evt.position);
            _dragCaptureElement?.ReleasePointer(_dragPointerId);
            ResetDragState();
        }

        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            if (_dragSourceKind == DragSourceKind.None)
            {
                return;
            }
            // 捕获意外丢失（非本控制器主动 ReleasePointer 触发）：只做视觉收尾，不触发任何装/卸/画边。
            ClearGhost();
            ClearHighlight();
            ResetDragState();
        }

        /// <summary>PointerUp 落点判定（D2/D3）：阈值内或边模式快照命中 → 点击语义；否则按来源路由。</summary>
        private void HandleDragEnd(Vector2 position)
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            ClearGhost();
            ClearHighlight();

            if (panel == null)
            {
                return;
            }

            if (_dragModeLocked || !_dragActive)
            {
                if (_dragSourceKind == DragSourceKind.Bag)
                {
                    panel.ToggleSelectPart(_dragPartId);
                }
                else if (_dragSourceKind == DragSourceKind.Slot)
                {
                    panel.HandleSlotClick(_dragFromSlot);
                }
                return;
            }

            if (_dragSourceKind == DragSourceKind.Slot && _dragOriginEmpty)
            {
                return;
            }

            if (_dragSourceKind == DragSourceKind.Bag)
            {
                int? targetSlot = HitTestSlot(position);
                if (targetSlot.HasValue)
                {
                    panel.DragEquip(_dragPartId, targetSlot.Value);
                }
                return;
            }

            if (_dragSourceKind == DragSourceKind.Slot)
            {
                if (HitTestBagArea(position))
                {
                    panel.DragUnequip(_dragFromSlot);
                    return;
                }
                int? targetSlot = HitTestSlot(position);
                if (targetSlot.HasValue && targetSlot.Value != _dragFromSlot && SlotGrid.IsAdjacent(_dragFromSlot, targetSlot.Value))
                {
                    panel.DragAddEdge(_dragFromSlot, targetSlot.Value);
                }
            }
        }

        private void ResetDragState()
        {
            _dragSourceKind = DragSourceKind.None;
            _dragPartId = null;
            _dragFromSlot = -1;
            _dragOriginEmpty = false;
            _dragActive = false;
            _dragModeLocked = false;
            _dragPointerId = -1;
            _dragCaptureElement = null;
        }

        private int? HitTestSlot(Vector2 position)
        {
            for (int i = 0; i < SlotGrid.SlotCount; i++)
            {
                Button slot = _slotButtons[i];
                if (slot != null && slot.worldBound.Contains(position))
                {
                    return i;
                }
            }
            return null;
        }

        /// <summary>D3 命中区域：Rect.MinMaxRect 合并 BagTitle 与 BagList 的 worldBound。</summary>
        private bool HitTestBagArea(Vector2 position)
        {
            if (_bagTitle == null && _bagList == null)
            {
                return false;
            }
            if (_bagTitle == null)
            {
                return _bagList.worldBound.Contains(position);
            }
            if (_bagList == null)
            {
                return _bagTitle.worldBound.Contains(position);
            }
            Rect a = _bagTitle.worldBound;
            Rect b = _bagList.worldBound;
            Rect union = Rect.MinMaxRect(
                Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
                Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
            return union.Contains(position);
        }

        /// <summary>D6 合法性预判：只用公开只读入口，不复制 SoftCap 等私有规则。</summary>
        private DropState EvaluateSlotDrop(MetabolicSlicePanel panel, int targetSlotId)
        {
            if (_dragSourceKind == DragSourceKind.Bag)
            {
                PartInstance item = panel.Bag.Items.Find(p => p.PartId == _dragPartId);
                SlotNode node = panel.Grid.Slots[targetSlotId];
                bool valid = item != null && node.IsEmpty && CardCatalog.AllowsSlot(item.CardDefId, node.SlotType);
                return valid ? DropState.Valid : DropState.Invalid;
            }

            if (_dragSourceKind == DragSourceKind.Slot)
            {
                int from = _dragFromSlot;
                if (targetSlotId == from || !SlotGrid.IsAdjacent(from, targetSlotId))
                {
                    return DropState.None;
                }
                foreach (DirectedEdge e in panel.Grid.Edges)
                {
                    if (e.From == from && e.To == targetSlotId)
                    {
                        return DropState.Invalid;
                    }
                }
                return DropState.Valid;
            }

            return DropState.None;
        }

        /// <summary>D4：拖影文字=来源件的显示名，跟手定位用 _root.WorldToLocal 换算。</summary>
        private void UpdateGhost(Vector2 worldPos)
        {
            if (_dragGhost == null)
            {
                return;
            }
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }

            string text;
            if (_dragSourceKind == DragSourceKind.Bag)
            {
                PartInstance item = panel.Bag.Items.Find(p => p.PartId == _dragPartId);
                text = item != null ? panel.DisplayName(item.CardDefId) : string.Empty;
            }
            else
            {
                SlotNode node = panel.Grid.Slots[_dragFromSlot];
                text = node.Part != null ? panel.DisplayName(node.Part.CardDefId) : string.Empty;
            }
            _dragGhost.text = text;

            Vector2 local = _root.WorldToLocal(worldPos);
            _dragGhost.style.left = local.x - 40f;
            _dragGhost.style.top = local.y - 12f;
            _dragGhost.style.display = DisplayStyle.Flex;
        }

        private void ClearGhost()
        {
            if (_dragGhost != null)
            {
                _dragGhost.style.display = DisplayStyle.None;
            }
        }

        /// <summary>D5：同一时刻最多一个元素带高亮 class，_lastHighlighted 追踪防止残留。</summary>
        private void UpdateHighlight(Vector2 worldPos)
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }

            VisualElement candidate = null;
            DropState state = DropState.None;

            int? targetSlot = HitTestSlot(worldPos);
            if (targetSlot.HasValue)
            {
                candidate = _slotButtons[targetSlot.Value];
                state = EvaluateSlotDrop(panel, targetSlot.Value);
            }

            if (candidate == _lastHighlighted)
            {
                return;
            }

            ClearHighlight();

            if (candidate != null && state != DropState.None)
            {
                candidate.AddToClassList(state == DropState.Valid ? "drop-valid" : "drop-invalid");
                _lastHighlighted = candidate;
            }
        }

        private void ClearHighlight()
        {
            if (_lastHighlighted != null)
            {
                _lastHighlighted.RemoveFromClassList("drop-valid");
                _lastHighlighted.RemoveFromClassList("drop-invalid");
                _lastHighlighted = null;
            }
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
