using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.Battle;
using GameLogic.UI.Common;
using TEngine;

namespace GameLogic
{
    /// <summary>
    /// UI Toolkit 版 Carrier 器官栏 + 插槽条（organ-socket-slice/story-005）。
    /// 独立控制器，不并入既有 BattleMetabolicUIToolkit（后者是 006 的整体拆除对象，D1）。
    /// 器官栏列 CarrierRegistry.All，点选切换激活；插槽条显示 ActiveCarrier.Slots（slot-unlimited-codex/002
    /// R1/R2 起动态可增长，滚动容器按 carrier.Slots.Count 运行时生成/清空节点，不再固定 3 格），
    /// 随激活切换刷新；基因拖放经 MetabolicSlicePanel.DragEquipGene/DragUnequipGene 转调
    /// CarrierGeneService（D7）。本 story 只做装/卸，不做丢弃（D16）。
    /// 照抄 BattleMetabolicUIToolkit 的 rootVisualElement 轮询保护（D3）+ 拖拽手势模式（D5/D8）。
    /// </summary>
    public class BattleCarrierUIToolkit : MonoBehaviour
    {
        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _carrierList;
        private ScrollView _slotBar;
        private ScrollView _geneList;
        private Label _noCarrierHint;
        private Button _organViewToggle;
        private Button _geneViewToggle;

        /// <summary>story-003（slot-unlimited-codex）R4：已拥有/全量 视图切换态，默认已拥有（行为不变）。</summary>
        private bool _showAllOrgans;
        private bool _showAllGenes;

        /// <summary>story-002 R2：槽位按钮改运行时按 carrier.Slots.Count 动态生成，不再固定数组。</summary>
        private readonly List<Button> _slotButtons = new List<Button>();

        /// <summary>零 Carrier 时渲染的禁用态空槽数（纯展示，D14 先例；与数据层 SlotSoftCap 无关）。</summary>
        private const int NoCarrierPlaceholderSlots = 3;

        // ---- D5/D8：拖拽手势（不含边模式分支，照搬阈值与捕获生命周期） ----
        private const float DragThreshold = 6f;
        private enum DragSourceKind { None, GeneList, Slot }
        private enum DropState { None, Valid, Invalid }

        private Label _dragGhost;
        private DragSourceKind _dragSourceKind = DragSourceKind.None;
        private string _dragGeneInstanceId;
        private int _dragFromSlot = -1;
        private bool _dragOriginEmpty;
        private bool _dragActive;
        private int _dragPointerId = -1;
        private VisualElement _dragCaptureElement;
        private VisualElement _lastHighlighted;
        private Vector2 _dragStartPos;

        // D11：GeneReserve.Items 只在事件时读，不得每帧读（landmine #3）
        private readonly List<GeneInstance> _reserveCache = new List<GeneInstance>();

        // story-003（topdown-hud-projectile-fix）R3/R4：默认隐藏，O 键 toggle；不再由 cell.IsRunning 强制常驻。
        private bool _panelOpen;

        /// <summary>供 BattleOverlayUIToolkit（Esc 互斥，短路判断）与 execute_code 断言跨控制器只读访问，同 BattleOverlayUIToolkit.Instance 先例。</summary>
        public static BattleCarrierUIToolkit Instance { get; private set; }

        /// <summary>只读探针：装配面板当前是否处于打开态。</summary>
        public bool IsPanelOpen => _panelOpen;

        /// <summary>供 BattleOverlayUIToolkit Esc 分支调用：先关本面板，不在同一次按键里弹 Pause（R4）。</summary>
        public void SetPanelOpen(bool open)
        {
            _panelOpen = open;
        }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BattleCarrierUI");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");

            if (this == null)
            {
                // 组件在异步加载期间被销毁（例如热更域重载）。
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // D2：sortingOrder = 4（实读现况 HUD=0/Metabolic=3/Draft=6/Overlay=10/Result=12，4 是空档）
            _document.sortingOrder = 4;

            // D3：照抄 rootVisualElement 轮询保护（10 帧 guard + 超时 LogError）
            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[BattleCarrierUIToolkit] rootVisualElement 等待超时，器官栏未初始化。");
                return;
            }
            CacheNodes();
            CreateDragGhost();
            SubscribeEvents();
            RefreshAll();
        }

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
            _carrierList = _root.Q<VisualElement>("CarrierList");
            _slotBar = _root.Q<ScrollView>("SlotBar");
            // story-003（carrier-organ-expansion）R4：contentContainer 加换行网格 class，SlotBar 从横向单行滚动
            // 改纵向换行网格，避免槽位数涨到 19 后横向挤压塌陷。
            _slotBar?.contentContainer.AddToClassList("slot-bar-content");
            _geneList = _root.Q<ScrollView>("GeneList");
            _noCarrierHint = _root.Q<Label>("NoCarrierHint");
            _organViewToggle = _root.Q<Button>("OrganViewToggle");
            _geneViewToggle = _root.Q<Button>("GeneViewToggle");

            // story-003（slot-unlimited-codex）R4：已拥有/全量 Tab，纯 UI 展示态切换，不写数据。
            if (_organViewToggle != null)
            {
                _organViewToggle.clicked += () => SetShowAllOrgans(!_showAllOrgans);
            }
            if (_geneViewToggle != null)
            {
                _geneViewToggle.clicked += () => SetShowAllGenes(!_showAllGenes);
            }

            // 用户要求全部面板可拖拽：面板根节点已被基因/器官拖拽手势占用（本文件的 D5/D8），
            // 不能复用根节点当把手，改用新增的 CarrierTitleBar 标题栏。
            VisualElement carrierPanel = _root.Q<VisualElement>("BattleCarrierUI");
            Label titleBar = _root.Q<Label>("CarrierTitleBar");
            if (carrierPanel != null && titleBar != null)
            {
                var drag = new PanelDragManipulator(titleBar, carrierPanel, "carrier");
                titleBar.AddManipulator(drag);
                drag.ApplyPersistedPosition();
            }

            // story-002 R2：槽位按钮不再由 UXML 预置 Slot0/1/2，改 RefreshSlotBar 运行时按
            // carrier.Slots.Count 动态生成/清空，事件回调随节点一并挂在生成处。
        }

        /// <summary>D13：订阅 CarrierActivatedEvent 刷新插槽条与高亮；story-001 R1 追加订阅
        /// MetabolicSlicePanel.InventoryChangedEvent（入囊基因/器官触发）；OnDestroy 时成对反订阅（GameEvent 纪律）。</summary>
        private void SubscribeEvents()
        {
            GameEvent.AddEventListener(CarrierRegistry.CarrierActivatedEvent, OnCarrierActivated);
            GameEvent.AddEventListener(MetabolicSlicePanel.InventoryChangedEvent, OnCarrierActivated);
        }

        private void OnCarrierActivated()
        {
            RefreshAll();
        }

        /// <summary>story-001 R4：只读探针，供 execute_code 断言用，不新增机制。</summary>
        public int VisibleCarrierCount => _carrierList?.childCount ?? 0;

        /// <summary>基因按钮计数（排除分组标题 Label）。</summary>
        public int VisibleGeneButtonCount => _geneList?.Query<Button>().ToList().Count ?? 0;

        /// <summary>插槽条是否可交互（0 号槽 enabledSelf 作为代表，同态）。</summary>
        public bool ActiveSlotBarEnabled => _slotButtons.Count > 0 && _slotButtons[0] != null && _slotButtons[0].enabledSelf;

        /// <summary>story-002 R2：只读探针，供 execute_code 断言插槽条实际生成的节点数（应等于 carrier.Slots.Count）。</summary>
        public int VisibleSlotButtonCount => _slotButtons.Count;

        /// <summary>story-003（slot-unlimited-codex）R4：只读探针 + 切换入口，供 execute_code 直调，不必模拟点击。</summary>
        public bool ShowAllOrgans => _showAllOrgans;

        /// <summary>同上，基因栏。</summary>
        public bool ShowAllGenes => _showAllGenes;

        /// <summary>切"已拥有/全量"器官视图；全量视图纯展示态，不调用 AddOrganPart/写 CarrierRegistry（D2）。</summary>
        public void SetShowAllOrgans(bool showAll)
        {
            _showAllOrgans = showAll;
            if (_organViewToggle != null)
            {
                _organViewToggle.text = showAll ? "已拥有" : "全量目录";
            }
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel != null)
            {
                RefreshCarrierList(panel);
            }
        }

        /// <summary>切"已拥有/全量"基因视图；全量视图纯展示态，不调用 AddGene/写 GeneReserve（D2）。</summary>
        public void SetShowAllGenes(bool showAll)
        {
            _showAllGenes = showAll;
            if (_geneViewToggle != null)
            {
                _geneViewToggle.text = showAll ? "已拥有" : "全量目录";
            }
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel != null)
            {
                RefreshGeneList(panel);
            }
        }

        /// <summary>story-003 #7：只读探针，供 execute_code 断言面板放大后的实际宽高。</summary>
        public float PanelWidth => _root?.Q<VisualElement>("BattleCarrierUI")?.resolvedStyle.width ?? 0f;

        /// <summary>story-003 #7：只读探针，供 execute_code 断言面板放大后的实际宽高。</summary>
        public float PanelHeight => _root?.Q<VisualElement>("BattleCarrierUI")?.resolvedStyle.height ?? 0f;

        /// <summary>story-003 R3/R4：O 键 toggle 开关；Draft 抽卡显示期间强制收起（不得与 Draft 面板并存）；
        /// Esc 关闭改由 BattleOverlayUIToolkit 统一入口调用 <see cref="SetPanelOpen"/>（短路判断，见该类 Update）。</summary>
        private void Update()
        {
            if (_root == null)
            {
                return;
            }

            CellStageFlow cell = GameRoot.CellStage;
            bool running = cell != null && cell.IsRunning;

            if (running)
            {
                if (cell.Paused && cell.PendingOptions != null && cell.PendingOptions.Count > 0)
                {
                    _panelOpen = false;
                }
                else if (Input.GetKeyDown(KeyCode.O))
                {
                    _panelOpen = !_panelOpen;
                }
            }
            else
            {
                _panelOpen = false;
            }

            _root.style.display = (running && _panelOpen) ? DisplayStyle.Flex : DisplayStyle.None;
            if (!running)
            {
                return;
            }
        }

        /// <summary>D11/D13/D14：刷新器官栏、插槽条、基因列表；零 Carrier 渲染禁用态提示。</summary>
        private void RefreshAll()
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }

            RefreshCarrierList(panel);
            RefreshSlotBar(panel);
            RefreshGeneList(panel);
        }

        private void RefreshCarrierList(MetabolicSlicePanel panel)
        {
            if (_carrierList == null)
            {
                return;
            }

            _carrierList.Clear();

            if (_showAllOrgans)
            {
                RefreshCarrierListAllCatalog();
                return;
            }

            CarrierRegistry registry = panel.CarrierRegistry;
            string activeId = registry.ActiveCarrierId;

            foreach (var kvp in registry.All)
            {
                string carrierId = kvp.Key;
                CarrierInstance carrier = kvp.Value;
                Button btn = new Button();
                btn.text = GetCarrierDisplayName(carrier);
                btn.AddToClassList("carrier-item");
                if (carrierId == activeId)
                {
                    btn.AddToClassList("carrier-active");
                }
                btn.clicked += () => registry.SetActive(carrierId);
                // story-005 R4：hover 器官图标显示 Description 摘要。
                string desc = carrier.OrganelleId != null ? OrganelleCatalog.Get(carrier.OrganelleId)?.Description : null;
                btn.RegisterCallback<PointerEnterEvent>(evt => BattleOverlayUIToolkit.Instance?.ShowTooltip(desc, evt.position));
                btn.RegisterCallback<PointerLeaveEvent>(evt => BattleOverlayUIToolkit.Instance?.HideTooltip());
                _carrierList.Add(btn);
            }
        }

        /// <summary>story-003（slot-unlimited-codex）D2：全量目录，数据源复用 CodexRegistry
        /// （V 键图鉴同一出口）。纯展示态：只读说明 + 点击切视觉高亮，不调用 SetActive/AddOrganPart，不写 CarrierRegistry/_bag。
        /// 任务一（器官重新分类）：拆两组渲染——先"载体器官"（AllCarrierOrganelleEntries），再"代谢模块"（AllMetabolicModuleEntries）。</summary>
        private void RefreshCarrierListAllCatalog()
        {
            CodexRegistry codex = GameRoot.CellStage?.Codex;
            if (codex == null)
            {
                return;
            }

            AddCarrierCatalogSection("载体器官", codex.AllCarrierOrganelleEntries());
            AddCarrierCatalogSection("代谢模块", codex.AllMetabolicModuleEntries());
        }

        private void AddCarrierCatalogSection(string sectionTitle, System.Collections.Generic.IEnumerable<OrganelleCodexEntry> entries)
        {
            Label title = new Label(sectionTitle);
            title.AddToClassList("gene-section-title");
            _carrierList.Add(title);

            foreach (OrganelleCodexEntry e in entries)
            {
                Button btn = new Button();
                btn.text = e.DisplayName ?? e.Id;
                btn.AddToClassList("carrier-item");
                btn.AddToClassList("carrier-catalog-item");
                string desc = e.Description;
                btn.RegisterCallback<PointerEnterEvent>(evt => BattleOverlayUIToolkit.Instance?.ShowTooltip(desc, evt.position));
                btn.RegisterCallback<PointerLeaveEvent>(evt => BattleOverlayUIToolkit.Instance?.HideTooltip());
                btn.clicked += () => TogglePreviewHighlight(btn);
                _carrierList.Add(btn);
            }
        }

        /// <summary>全量目录条目的"试装预览"：纯视觉高亮切换，不触发任何数据写入。</summary>
        private static void TogglePreviewHighlight(VisualElement element)
        {
            if (element.ClassListContains("catalog-preview-selected"))
            {
                element.RemoveFromClassList("catalog-preview-selected");
            }
            else
            {
                element.AddToClassList("catalog-preview-selected");
            }
        }

        /// <summary>D14：零 Carrier 时渲染禁用态空格 + 提示（不隐藏整条，防布局塌陷）。
        /// story-002 R2：插槽条改运行时动态生成，按钮数 = hasCarrier ? carrier.Slots.Count : NoCarrierPlaceholderSlots；
        /// 拖拽起点在槽位时冻结（同 RefreshGeneList 对 GeneList 的处理），防止销毁正在捕获的元素。</summary>
        private void RefreshSlotBar(MetabolicSlicePanel panel)
        {
            if (_slotBar == null)
            {
                return;
            }
            if (_dragSourceKind == DragSourceKind.Slot)
            {
                return;
            }

            CarrierInstance carrier = panel.CarrierRegistry.ActiveCarrier;
            bool hasCarrier = carrier != null;

            if (_noCarrierHint != null)
            {
                _noCarrierHint.style.display = hasCarrier ? DisplayStyle.None : DisplayStyle.Flex;
            }

            _slotBar.Clear();
            _slotButtons.Clear();

            int count = hasCarrier ? carrier.Slots.Count : NoCarrierPlaceholderSlots;
            for (int i = 0; i < count; i++)
            {
                int slotIndex = i;
                Button slot = new Button();
                slot.AddToClassList("slot-cell-fixed");
                _slotBar.Add(slot);
                _slotButtons.Add(slot);

                if (!hasCarrier)
                {
                    // D14：零 Carrier 渲染禁用态空格
                    slot.text = $"[{i}] —";
                    slot.SetEnabled(false);
                    slot.AddToClassList("slot-empty");
                    continue;
                }

                slot.SetEnabled(true);
                CarrierSlot cs = carrier.Slots[i];
                bool occupied = cs.GeneInstanceId != null;
                slot.text = occupied ? GetSlotGeneDisplayName(panel, cs.GeneInstanceId) : $"[{i}] 空";
                slot.AddToClassList(occupied ? "slot-filled" : "slot-empty");

                slot.RegisterCallback<PointerDownEvent>(evt => OnSlotPointerDown(evt, slot, slotIndex));
                slot.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                slot.RegisterCallback<PointerUpEvent>(OnPointerUp);
                slot.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
                // story-005 R4：hover 已装备槽位显示基因 Description 摘要，动态查最新装备。
                slot.RegisterCallback<PointerEnterEvent>(evt => OnSlotPointerEnter(evt, slotIndex));
                slot.RegisterCallback<PointerLeaveEvent>(evt => BattleOverlayUIToolkit.Instance?.HideTooltip());
            }
        }

        /// <summary>D9/D10/D11：列表数据源 = GeneCatalog.AllGeneIds（30 条全集），Contract 段在前、Module 段在后并加分组标题。
        /// GeneReserve.Items 只在此刻读一次，存入 _reserveCache（D11，landmine #3）。</summary>
        private void RefreshGeneList(MetabolicSlicePanel panel)
        {
            if (_geneList == null)
            {
                return;
            }

            if (_dragSourceKind == DragSourceKind.GeneList)
            {
                // 拖拽起点是列表内某按钮，冻结列表防止意外销毁正在捕获的元素
                return;
            }

            _geneList.Clear();

            if (_showAllGenes)
            {
                RefreshGeneListAllCatalog();
                return;
            }

            _reserveCache.Clear();
            _reserveCache.AddRange(panel.GeneReserve.Items);

            // D10：先 11 条 Contract 再 19 条 Module，各自保持目录声明序，两段之间插分组标题
            AddGeneSection("契约基因", GeneCatalog.AllIds);
            AddGeneSection("模块基因", GeneCatalog.AllModuleIds);
        }

        /// <summary>story-003（slot-unlimited-codex）D2：全量目录（30 基因），数据源复用 CodexRegistry.AllGeneEntries()
        /// （V 键图鉴同一出口）。纯展示态：只读说明 + 点击切视觉高亮，不调用 DragEquipGene，不写 GeneReserve/_bag。</summary>
        private void RefreshGeneListAllCatalog()
        {
            CodexRegistry codex = GameRoot.CellStage?.Codex;
            if (codex == null)
            {
                return;
            }

            foreach (GeneCodexEntry e in codex.AllGeneEntries())
            {
                Button btn = new Button();
                btn.text = e.DisplayName ?? e.Id;
                btn.AddToClassList("gene-item");
                btn.AddToClassList("gene-catalog-item");
                string desc = e.Description;
                btn.RegisterCallback<PointerEnterEvent>(evt => BattleOverlayUIToolkit.Instance?.ShowTooltip(desc, evt.position));
                btn.RegisterCallback<PointerLeaveEvent>(evt => BattleOverlayUIToolkit.Instance?.HideTooltip());
                btn.clicked += () => TogglePreviewHighlight(btn);
                _geneList.Add(btn);
            }
        }

        private void AddGeneSection(string sectionTitle, System.Collections.Generic.IEnumerable<string> geneIds)
        {
            Label title = new Label(sectionTitle);
            title.AddToClassList("gene-section-title");
            _geneList.Add(title);

            foreach (string geneId in geneIds)
            {
                // 从 _reserveCache 里找该基因的所有实例
                foreach (GeneInstance gi in _reserveCache)
                {
                    if (gi.GeneId != geneId)
                    {
                        continue;
                    }
                    Button btn = new Button();
                    btn.text = GeneCatalog.GetDisplayName(geneId) ?? geneId;
                    btn.AddToClassList("gene-item");
                    string instanceId = gi.GeneInstanceId;
                    btn.RegisterCallback<PointerDownEvent>(evt => OnGenePointerDown(evt, btn, instanceId));
                    btn.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                    btn.RegisterCallback<PointerUpEvent>(OnPointerUp);
                    btn.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
                    // story-005 R4：hover 基因图标显示 Description 摘要。
                    string desc = GeneCatalog.GetDescription(geneId);
                    btn.RegisterCallback<PointerEnterEvent>(evt => BattleOverlayUIToolkit.Instance?.ShowTooltip(desc, evt.position));
                    btn.RegisterCallback<PointerLeaveEvent>(evt => BattleOverlayUIToolkit.Instance?.HideTooltip());
                    _geneList.Add(btn);
                }
            }
        }

        private string GetCarrierDisplayName(CarrierInstance carrier)
        {
            if (carrier.OrganelleId != null)
            {
                string name = OrganelleCatalog.Get(carrier.OrganelleId)?.DisplayName;
                if (name != null)
                {
                    return name;
                }
            }
            return carrier.CarrierId;
        }

        private string GetSlotGeneDisplayName(MetabolicSlicePanel panel, string geneInstanceId)
        {
            GeneInstance gi = panel.GeneReserve.Find(geneInstanceId);
            if (gi != null)
            {
                return GeneCatalog.GetDisplayName(gi.GeneId) ?? gi.GeneId;
            }
            return geneInstanceId;
        }

        // ==== D5/D8：拖拽手势（按模式照搬，不含边模式分支） ====

        /// <summary>story-005 R4：hover 已装备槽位 -> 查最新装备的基因，显示 Description 摘要；空槽不显示。</summary>
        private void OnSlotPointerEnter(PointerEnterEvent evt, int slotIndex)
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            CarrierInstance carrier = panel?.CarrierRegistry.ActiveCarrier;
            string geneInstanceId = carrier?.Slots[slotIndex].GeneInstanceId;
            if (geneInstanceId == null)
            {
                return;
            }
            GeneInstance gi = panel.GeneReserve.Find(geneInstanceId);
            if (gi == null)
            {
                return;
            }
            BattleOverlayUIToolkit.Instance?.ShowTooltip(GeneCatalog.GetDescription(gi.GeneId), evt.position);
        }

        private void OnSlotPointerDown(PointerDownEvent evt, Button slot, int slotIndex)
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }
            CarrierInstance carrier = panel.CarrierRegistry.ActiveCarrier;
            bool originEmpty = carrier == null || carrier.Slots[slotIndex].GeneInstanceId == null;
            BeginDrag(DragSourceKind.Slot, slotIndex, null, originEmpty, slot, evt);
        }

        private void OnGenePointerDown(PointerDownEvent evt, Button button, string geneInstanceId)
        {
            BeginDrag(DragSourceKind.GeneList, -1, geneInstanceId, false, button, evt);
        }

        private void BeginDrag(DragSourceKind kind, int slotIndex, string geneInstanceId, bool originEmpty, VisualElement element, PointerDownEvent evt)
        {
            _dragSourceKind = kind;
            _dragFromSlot = slotIndex;
            _dragGeneInstanceId = geneInstanceId;
            _dragOriginEmpty = originEmpty;
            _dragStartPos = evt.position;
            _dragActive = false;
            _dragPointerId = evt.pointerId;
            _dragCaptureElement = element;
            element.CapturePointer(evt.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_dragSourceKind == DragSourceKind.None)
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
                // 起点空槽，越过阈值也是无操作占位，不显示拖影/高亮
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
            // 捕获意外丢失：只做视觉收尾，不触发任何装/卸
            ClearGhost();
            ClearHighlight();
            ResetDragState();
        }

        /// <summary>D15：PointerUp 落点判定；阈值内 -> 无操作；否则按来源路由。拒绝时给出可见反馈。</summary>
        private void HandleDragEnd(Vector2 position)
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            ClearGhost();
            ClearHighlight();

            if (panel == null)
            {
                return;
            }

            if (!_dragActive)
            {
                // 未越过阈值，无操作
                return;
            }

            if (_dragSourceKind == DragSourceKind.Slot && _dragOriginEmpty)
            {
                return;
            }

            if (_dragSourceKind == DragSourceKind.GeneList)
            {
                int? targetSlot = HitTestSlot(position);
                if (targetSlot.HasValue)
                {
                    CarrierGeneResult result = panel.DragEquipGene(_dragGeneInstanceId, targetSlot.Value);
                    if (result != CarrierGeneResult.Ok)
                    {
                        ShowFeedback(result);
                    }
                    else
                    {
                        RefreshAll();
                    }
                }
                return;
            }

            if (_dragSourceKind == DragSourceKind.Slot)
            {
                // 拖出到列表区域 = 卸下
                if (HitTestGeneListArea(position))
                {
                    CarrierGeneResult result = panel.DragUnequipGene(_dragFromSlot);
                    if (result != CarrierGeneResult.Ok)
                    {
                        ShowFeedback(result);
                    }
                    else
                    {
                        RefreshAll();
                    }
                }
            }
        }

        /// <summary>D15：拒绝反馈必须可见，尤其 SlotOccupied 要明确「占用槽拒绝且不交换」。</summary>
        private void ShowFeedback(CarrierGeneResult result)
        {
            string message = result switch
            {
                CarrierGeneResult.GeneNotFound => "基因未找到",
                CarrierGeneResult.GeneAlreadyEquipped => "该基因已装备在其它槽位",
                CarrierGeneResult.SlotOccupied => "槽位已占用（不交换）",
                CarrierGeneResult.SlotIndexInvalid => "槽位索引无效",
                CarrierGeneResult.CarrierNotFound => "未选中器官",
                _ => "操作失败"
            };
            Debug.Log($"[BattleCarrierUIToolkit] {message}");
        }

        private void ResetDragState()
        {
            _dragSourceKind = DragSourceKind.None;
            _dragGeneInstanceId = null;
            _dragFromSlot = -1;
            _dragOriginEmpty = false;
            _dragActive = false;
            _dragPointerId = -1;
            _dragCaptureElement = null;
        }

        private int? HitTestSlot(Vector2 position)
        {
            for (int i = 0; i < _slotButtons.Count; i++)
            {
                Button slot = _slotButtons[i];
                if (slot != null && slot.worldBound.Contains(position))
                {
                    return i;
                }
            }
            return null;
        }

        private bool HitTestGeneListArea(Vector2 position)
        {
            return _geneList != null && _geneList.worldBound.Contains(position);
        }

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

            string text = string.Empty;
            if (_dragSourceKind == DragSourceKind.GeneList)
            {
                GeneInstance gi = panel.GeneReserve.Find(_dragGeneInstanceId);
                text = gi != null ? (GeneCatalog.GetDisplayName(gi.GeneId) ?? gi.GeneId) : string.Empty;
            }
            else if (_dragSourceKind == DragSourceKind.Slot)
            {
                CarrierInstance carrier = panel.CarrierRegistry.ActiveCarrier;
                if (carrier != null && _dragFromSlot >= 0 && _dragFromSlot < carrier.Slots.Count)
                {
                    string gid = carrier.Slots[_dragFromSlot].GeneInstanceId;
                    if (gid != null)
                    {
                        text = GetSlotGeneDisplayName(panel, gid);
                    }
                }
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

        private DropState EvaluateSlotDrop(MetabolicSlicePanel panel, int targetSlotIndex)
        {
            CarrierInstance carrier = panel.CarrierRegistry.ActiveCarrier;
            if (carrier == null)
            {
                return DropState.Invalid;
            }

            if (_dragSourceKind == DragSourceKind.GeneList)
            {
                GeneInstance gi = panel.GeneReserve.Find(_dragGeneInstanceId);
                if (gi == null || !gi.Location.IsInReserve)
                {
                    return DropState.Invalid;
                }
                CarrierSlot slot = carrier.Slots[targetSlotIndex];
                // D12：占用槽拒绝，不交换
                return slot.GeneInstanceId == null ? DropState.Valid : DropState.Invalid;
            }

            if (_dragSourceKind == DragSourceKind.Slot)
            {
                // 槽到槽不支持（本 story 不做槽内交换）
                return DropState.None;
            }

            return DropState.None;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            // D13：成对反订阅 GameEvent；story-001 R1 同步反订阅 InventoryChangedEvent
            GameEvent.RemoveEventListener(CarrierRegistry.CarrierActivatedEvent, OnCarrierActivated);
            GameEvent.RemoveEventListener(MetabolicSlicePanel.InventoryChangedEvent, OnCarrierActivated);

            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
