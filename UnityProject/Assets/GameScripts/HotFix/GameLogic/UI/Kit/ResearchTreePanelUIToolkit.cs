using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG5-RND-01（FG05 FGR-RND-013 研发树界面；FG13 FGU-22 研发树：缩放、搜索、队列；FG05 第 4 节“研发树有快捷键可以直接打开”）：研发树面板。
    /// - 画布：按节点表的分支（车道，从上到下）/ 列 / 行摆节点，前置连线用 Painter2D 画；滚轮以光标为中心缩放（research.tree.zoom_min～max），拖空白处平移，“复位”回到左上。
    /// - 搜索：名字、说明、解锁内容、分支名包含关键字即命中——命中的描金边，其余变暗，镜头移到第一个命中；没有命中写明。
    /// - 按分支筛选：只显示一个分支（车道收拢）；阵营分支没开放时按钮与车道写明“破解该阵营第一件技术后开放”。
    /// - 节点：左键加入队列（不能加时写原因）、右键移出（进度保留）；悬停在右侧详情显示它会解锁什么（占位图标、说明、建筑占地、前置、关键材料、状态与原因），并带悬停提示（按图鉴键跳到图鉴条目）。
    /// - 研究队列：最多 research.queue.max 项；按住拖动调整顺序，也可以用上移 / 下移 / 移出按钮；不合法的顺序（排到前置前面）被拒并写原因。
    /// 入口：快捷键（默认 K，可重绑）、顶栏研究点、“研究完成 / 研究暂停”通知。模态（Esc / 关闭 / 点遮罩关闭）。
    /// 节点与文字只在研究版本、搜索、筛选、语言、选中变化时刷新（O(节点数)），状态行每 0.5 真实秒重读一次；布局只在筛选 / 表变化时重算。
    /// </summary>
    public sealed class ResearchTreePanelUIToolkit : UiKitPanelHost
    {
        /// <summary>暂停菜单 30070 之下、HUD 之上：从研发树里点“?”打开的图鉴（30075）盖在它上面。</summary>
        public const int Order = 30069;
        private const float NodeWidth = 210f;
        private const float NodeHeight = 54f;
        private const float LeftMargin = 10f;

        public static ResearchTreePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;
        private static string _pendingFocus;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;
        /// <summary>自检：编辑模式下视口没有布局时用的尺寸。</summary>
        public static Vector2 ViewportSizeForTests = new Vector2(1000f, 620f);
        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        private VisualElement _root, _viewport, _canvas, _links, _detailIcon, _footprint, _footprintBox, _queueList;
        private Label _title, _summary, _status, _message, _searchCount, _zoomLabel, _detailGlyph, _detailTitle, _detailNote, _detailBody, _footprintText,
            _queueTitle, _queueEmpty, _queueTip, _footer;
        private Button _close, _zoomIn, _zoomOut, _zoomReset, _detailAction;
        private ScrollView _filters;
        private UiSearchBox _search;
        private VisualTreeAsset _nodeTemplate, _rowTemplate;
        private bool _templatesLoading;

        private sealed class NodeView
        {
            public ResearchNodeDef Def;
            public TemplateContainer Root;
            public Button Button;
            public VisualElement Stripe;
            public VisualElement Fill;
            public Label Name;
            public Label State;
            public Vector2 Pos;
            public bool Visible;
            public bool Match;
        }

        private sealed class RowView
        {
            public TemplateContainer Root;
            public VisualElement Row;
            public Label Text;
            public Button Up, Down, Remove;
            public string NodeId;
        }

        private readonly List<NodeView> _nodeViews = new List<NodeView>(80);
        private readonly Dictionary<string, NodeView> _nodeById = new Dictionary<string, NodeView>(StringComparer.Ordinal);
        private readonly List<Label> _laneLabels = new List<Label>(12);
        private readonly List<Button> _filterButtons = new List<Button>(12);
        private readonly List<string> _filterIds = new List<string>(12);
        private readonly List<RowView> _rows = new List<RowView>(5);

        private string _filter;
        private string _searchText = string.Empty;
        private float _zoom = 1f;
        private Vector2 _pan = new Vector2(0f, 0f);
        private Vector2 _canvasSize;
        private string _hoverId;
        private string _selectedId;
        private string _messageText = string.Empty;
        private bool _messageOk;
        private int? _lastKey;
        private int _layoutKey = int.MinValue;
        private double _nextReread;
        private bool _panning;
        private int _panPointer = -1;
        private Vector2 _panStart, _panOrigin;
        private int _dragFrom = -1;
        private int _dragPointer = -1;
        private bool _listening;

        protected override string UxmlLocation => "ResearchTreePanel";
        protected override int SortingOrder => Order;

        // ── 自检读数 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public float Zoom => _zoom;
        public Vector2 PanOffset => _pan;
        public string Filter => _filter;
        public string SearchText => _searchText;
        public int VisibleNodeCount { get; private set; }
        public int MatchCount { get; private set; }
        public string FirstMatchId { get; private set; }
        public string DetailNodeId { get; private set; }
        public string DetailTitle => _detailTitle?.text ?? string.Empty;
        public string DetailBody => _detailBody?.text ?? string.Empty;
        public string DetailNote => _detailNote?.text ?? string.Empty;
        public bool FootprintShown => _footprint != null && !_footprint.ClassListContains("uk-hidden");
        public string FootprintText => _footprintText?.text ?? string.Empty;
        public Button DetailAction => _detailAction;
        public string StatusText => _status?.text ?? string.Empty;
        public string SummaryText => _summary?.text ?? string.Empty;
        public string MessageText => _messageText;
        public string SearchCountText => _searchCount?.text ?? string.Empty;
        public string QueueTitleText => _queueTitle?.text ?? string.Empty;
        public string QueueEmptyText => _queueEmpty != null && !_queueEmpty.ClassListContains("uk-hidden") ? _queueEmpty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string ZoomText => _zoomLabel?.text ?? string.Empty;
        public int NodeViewCount => _nodeViews.Count;
        public int FilterButtonCount => _filterButtons.Count;
        public Button FilterButton(int i) => i >= 0 && i < _filterButtons.Count ? _filterButtons[i] : null;
        public string FilterId(int i) => i >= 0 && i < _filterIds.Count ? _filterIds[i] : null;
        public Button NodeButton(string id) => _nodeById.TryGetValue(id ?? string.Empty, out NodeView v) ? v.Button : null;
        public string NodeStateText(string id) => _nodeById.TryGetValue(id ?? string.Empty, out NodeView v) ? v.State.text : string.Empty;
        public bool NodeVisible(string id) => _nodeById.TryGetValue(id ?? string.Empty, out NodeView v) && v.Visible;
        public bool NodeHasClass(string id, string cls) => _nodeById.TryGetValue(id ?? string.Empty, out NodeView v) && v.Root.ClassListContains(cls);
        public Vector2 NodePosition(string id) => _nodeById.TryGetValue(id ?? string.Empty, out NodeView v) ? v.Pos : Vector2.zero;
        public float NodeFill(string id) => _nodeById.TryGetValue(id ?? string.Empty, out NodeView v) ? v.Fill.style.width.value.value : 0f;
        public int QueueRowVisibleCount { get; private set; }
        public string QueueRowText(int i) => i >= 0 && i < _rows.Count ? _rows[i].Text.text : string.Empty;
        public string QueueRowNode(int i) => i >= 0 && i < _rows.Count ? _rows[i].NodeId : null;
        public Button QueueRowButton(int i, int which) => i < 0 || i >= _rows.Count ? null : which == 0 ? _rows[i].Up : which == 1 ? _rows[i].Down : _rows[i].Remove;
        public VisualElement QueueRowElement(int i) => i >= 0 && i < _rows.Count ? _rows[i].Row : null;
        public string LaneText(int branchIndex) => branchIndex >= 0 && branchIndex < _laneLabels.Count ? _laneLabels[branchIndex].text : string.Empty;
        public bool LaneVisible(int branchIndex) => branchIndex >= 0 && branchIndex < _laneLabels.Count && !_laneLabels[branchIndex].ClassListContains("uk-hidden");
        public int LinkCount { get; private set; }
        public int RefreshCount { get; private set; }
        public int LayoutCount { get; private set; }

        private void Awake()
        {
            Instance = this;
            Listen();
        }

        private void Listen()
        {
            if (_listening)
            {
                return;
            }
            _listening = true;
            NotificationCenter.RegisterOpenHandler("research_done", OpenFromNotification);
            NotificationCenter.RegisterOpenHandler("research_stalled", OpenFromNotification);
        }

        private static bool OpenFromNotification(NotificationEntry entry)
        {
            Open();
            return true;
        }

        protected override void OnDestroy()
        {
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            if (_nodeTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_nodeTemplate);
                _nodeTemplate = null;
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

        // ── 打开 / 关闭 ──────────────────────────────────────────────────────

        public static void Open() => OpenAt(null);

        /// <summary>打开并选中 <paramref name="nodeId"/>（镜头移到它；为空时保持上次的视图）。</summary>
        public static void OpenAt(string nodeId)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingFocus = nodeId;
                return;
            }
            Instance.SetOpen(true);
            if (!string.IsNullOrEmpty(nodeId))
            {
                Instance.Select(nodeId);
                Instance.CenterOn(nodeId);
            }
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadTemplates().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                OpenAt(_pendingFocus);
            }
        }

        private async UniTaskVoid LoadTemplates()
        {
            if (_templatesLoading || (_nodeTemplate != null && _rowTemplate != null))
            {
                return;
            }
            _templatesLoading = true;
            VisualTreeAsset node = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("ResearchNode");
            VisualTreeAsset row = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("ResearchQueueRow");
            _templatesLoading = false;
            if (this == null)
            {
                if (node != null)
                {
                    GameModule.Resource.UnloadAsset(node);
                }
                if (row != null)
                {
                    GameModule.Resource.UnloadAsset(row);
                }
                return;
            }
            if (node == null || row == null)
            {
                Log.Error("[ResearchTreePanelUIToolkit] 加载 ResearchNode / ResearchQueueRow 失败，研发树的节点与队列不可用。");
            }
            _nodeTemplate = node;
            _rowTemplate = row;
            _lastKey = null;
            _layoutKey = int.MinValue;
            Refresh();
        }

        /// <summary>自检：编辑模式下直接给模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetTemplatesForTests(VisualTreeAsset node, VisualTreeAsset row)
        {
            _nodeTemplate = node;
            _rowTemplate = row;
            _lastKey = null;
            _layoutKey = int.MinValue;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            Listen();
            _root = root.Q<VisualElement>("ResearchRoot");
            _title = root.Q<Label>("ResearchTitle");
            _summary = root.Q<Label>("ResearchSummary");
            _close = root.Q<Button>("ResearchClose");
            _searchCount = root.Q<Label>("ResearchSearchCount");
            _zoomOut = root.Q<Button>("ResearchZoomOut");
            _zoomIn = root.Q<Button>("ResearchZoomIn");
            _zoomReset = root.Q<Button>("ResearchZoomReset");
            _zoomLabel = root.Q<Label>("ResearchZoomLabel");
            _filters = root.Q<ScrollView>("ResearchFilters");
            _status = root.Q<Label>("ResearchStatus");
            _message = root.Q<Label>("ResearchMessage");
            _viewport = root.Q<VisualElement>("ResearchViewport");
            _canvas = root.Q<VisualElement>("ResearchCanvas");
            _links = root.Q<VisualElement>("ResearchLinks");
            _detailIcon = root.Q<VisualElement>("ResearchDetailIcon");
            _detailGlyph = root.Q<Label>("ResearchDetailGlyph");
            _detailTitle = root.Q<Label>("ResearchDetailTitle");
            _detailNote = root.Q<Label>("ResearchDetailIconNote");
            _footprint = root.Q<VisualElement>("ResearchDetailFootprint");
            _footprintBox = root.Q<VisualElement>("ResearchDetailFootprintBox");
            _footprintText = root.Q<Label>("ResearchDetailFootprintText");
            _detailBody = root.Q<Label>("ResearchDetailBody");
            _detailAction = root.Q<Button>("ResearchDetailAction");
            _queueTitle = root.Q<Label>("ResearchQueueTitle");
            _queueEmpty = root.Q<Label>("ResearchQueueEmpty");
            _queueList = root.Q<VisualElement>("ResearchQueueList");
            _queueTip = root.Q<Label>("ResearchQueueTip");
            _footer = root.Q<Label>("ResearchFooter");

            _close.clicked += () => SetOpen(false);
            _zoomIn.clicked += () => ZoomBy(1, ViewportCenter());
            _zoomOut.clicked += () => ZoomBy(-1, ViewportCenter());
            _zoomReset.clicked += ResetView;
            _detailAction.clicked += OnDetailAction;
            _search = new UiSearchBox(root.Q<TextField>("ResearchSearch"), root.Q<Label>("ResearchSearchPlaceholder"), root.Q<Button>("ResearchSearchClear"),
                "research.panel.search", SetSearch);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _viewport.RegisterCallback<WheelEvent>(evt =>
            {
                ZoomBy(evt.delta.y > 0f ? -1 : 1, evt.localMousePosition);
                evt.StopPropagation();
            });
            _viewport.RegisterCallback<PointerDownEvent>(OnViewportDown);
            _viewport.RegisterCallback<PointerMoveEvent>(OnViewportMove);
            _viewport.RegisterCallback<PointerUpEvent>(OnViewportUp);
            _links.generateVisualContent += DrawLinks;
            UiTooltip.Attach(_detailAction, () => new TooltipContent { Title = _detailAction.text, Body = GameText.Get("research.panel.queue_tip") });
            _nodeViews.Clear();
            _nodeById.Clear();
            _laneLabels.Clear();
            _filterButtons.Clear();
            _filterIds.Clear();
            _rows.Clear();
            _lastKey = null;
            _layoutKey = int.MinValue;
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
                GuidanceHooks.Raise(GuidanceHooks.ResearchTreeFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _messageText = string.Empty;
                _lastKey = null;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                UiTooltip.Hide();
                _panning = false;
                _dragFrom = -1;
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
            Refresh();
        }

        // ── 视图操作（玩家输入与自检共用）──────────────────────────────────────

        public void SetSearch(string text)
        {
            _searchText = (text ?? string.Empty).Trim();
            _lastKey = null;
            Refresh();
            if (FirstMatchId != null)
            {
                CenterOn(FirstMatchId);
            }
        }

        /// <summary>按分支筛选；null = 全部分支。</summary>
        public void SetFilter(string branchId)
        {
            _filter = string.IsNullOrEmpty(branchId) ? null : branchId;
            _lastKey = null;
            Refresh();
            ResetView();
        }

        private float ZoomMin => Mathf.Max(0.1f, GridContent.Tuning("research.tree.zoom_min"));
        private float ZoomMax => Mathf.Max(ZoomMin, GridContent.Tuning("research.tree.zoom_max"));
        private float ZoomStep => Mathf.Max(0.01f, GridContent.Tuning("research.tree.zoom_step"));

        /// <summary>以视口内一点（局部坐标）为中心缩放 <paramref name="steps"/> 档（正 = 放大）；夹在 zoom_min～zoom_max。</summary>
        public void ZoomBy(int steps, Vector2 around)
        {
            float old = _zoom;
            float z = Mathf.Clamp(Mathf.Round((_zoom + steps * ZoomStep) * 100f) / 100f, ZoomMin, ZoomMax);
            if (Mathf.Approximately(z, old))
            {
                return;
            }
            // 光标下那一点在缩放前后保持不动。
            _pan = around - (around - _pan) * (z / old);
            _zoom = z;
            ClampPan();
            ApplyTransform();
        }

        public void PanBy(Vector2 delta)
        {
            _pan += delta;
            ClampPan();
            ApplyTransform();
        }

        public void ResetView()
        {
            _zoom = 1f;
            _pan = Vector2.zero;
            ApplyTransform();
        }

        /// <summary>把节点移到视口中间。</summary>
        public void CenterOn(string nodeId)
        {
            if (!_nodeById.TryGetValue(nodeId ?? string.Empty, out NodeView v))
            {
                return;
            }
            Vector2 center = ViewportCenter();
            _pan = center - (v.Pos + new Vector2(NodeWidth * 0.5f, NodeHeight * 0.5f)) * _zoom;
            ClampPan();
            ApplyTransform();
        }

        private Vector2 ViewportSize()
        {
            Rect r = _viewport?.contentRect ?? Rect.zero;
            return float.IsNaN(r.width) || r.width < 10f || float.IsNaN(r.height) || r.height < 10f ? ViewportSizeForTests : r.size;
        }

        private Vector2 ViewportCenter() => ViewportSize() * 0.5f;

        /// <summary>平移不让画布整块跑出视口（至少留 80 像素看得见）。</summary>
        private void ClampPan()
        {
            Vector2 view = ViewportSize();
            const float keep = 80f;
            float minX = keep - _canvasSize.x * _zoom;
            float minY = keep - _canvasSize.y * _zoom;
            _pan.x = Mathf.Clamp(_pan.x, Mathf.Min(minX, 0f), Mathf.Max(view.x - keep, 0f));
            _pan.y = Mathf.Clamp(_pan.y, Mathf.Min(minY, 0f), Mathf.Max(view.y - keep, 0f));
        }

        private void ApplyTransform()
        {
            if (_canvas == null)
            {
                return;
            }
            // 缩放 / 平移是运行时数据驱动的数值（红线 2 允许）：transform-origin 在 USS 里定为左上角。
            _canvas.style.translate = new Translate(_pan.x, _pan.y);
            _canvas.style.scale = new Scale(new Vector3(_zoom, _zoom, 1f));
            if (_zoomLabel != null)
            {
                _zoomLabel.text = GameText.Format("research.panel.zoom", Mathf.RoundToInt(_zoom * 100f));
            }
        }

        private static bool InsideNode(IEventHandler target)
        {
            for (var ve = target as VisualElement; ve != null; ve = ve.parent)
            {
                if (ve.ClassListContains("rt-node"))
                {
                    return true;
                }
            }
            return false;
        }

        private void OnViewportDown(PointerDownEvent evt)
        {
            if (InsideNode(evt.target))
            {
                return;
            }
            _panning = true;
            _panPointer = evt.pointerId;
            _panStart = evt.position;
            _panOrigin = _pan;
            _viewport.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnViewportMove(PointerMoveEvent evt)
        {
            if (!_panning || evt.pointerId != _panPointer)
            {
                return;
            }
            _pan = _panOrigin + ((Vector2)evt.position - _panStart);
            ClampPan();
            ApplyTransform();
        }

        private void OnViewportUp(PointerUpEvent evt)
        {
            if (!_panning || evt.pointerId != _panPointer)
            {
                return;
            }
            _panning = false;
            if (_viewport.HasPointerCapture(evt.pointerId))
            {
                _viewport.ReleasePointer(evt.pointerId);
            }
        }

        // ── 节点交互 ─────────────────────────────────────────────────────────

        public void Select(string nodeId)
        {
            _selectedId = nodeId;
            _lastKey = null;
            Refresh();
        }

        public void HoverNode(string nodeId)
        {
            _hoverId = nodeId;
            ShowDetail(CampaignSession.Current, _hoverId ?? _selectedId);
        }

        /// <summary>左键：选中并加入队列（已完成的只选中）。</summary>
        public void ClickNode(string nodeId)
        {
            CampaignState state = CampaignSession.Current;
            _selectedId = nodeId;
            if (state != null && ResearchCatalog.TryGet(nodeId, out ResearchNodeDef n) && !ResearchService.IsCompleted(state, nodeId)
                && ResearchService.QueueIndex(state, nodeId) < 0)
            {
                if (ResearchService.TryEnqueue(state, nodeId, out string reason))
                {
                    SetMessage(GameText.Format("research.panel.message_queued", n.Name), true);
                }
                else
                {
                    SetMessage(GameText.Format("research.panel.message_refused", reason), false);
                }
            }
            _lastKey = null;
            Refresh();
        }

        /// <summary>右键：移出队列（已投入的进度保留在节点上）。</summary>
        public void RightClickNode(string nodeId)
        {
            CampaignState state = CampaignSession.Current;
            _selectedId = nodeId;
            if (state != null && ResearchCatalog.TryGet(nodeId, out ResearchNodeDef n) && ResearchService.QueueIndex(state, nodeId) >= 0)
            {
                ResearchService.TryDequeue(state, nodeId, out _);
                SetMessage(GameText.Format("research.panel.message_removed", n.Name, ResearchService.Invested(state, nodeId), n.Cost), true);
            }
            _lastKey = null;
            Refresh();
        }

        private void OnDetailAction()
        {
            string id = DetailNodeId;
            CampaignState state = CampaignSession.Current;
            if (id == null || state == null)
            {
                return;
            }
            if (ResearchService.QueueIndex(state, id) >= 0)
            {
                RightClickNode(id);
            }
            else
            {
                ClickNode(id);
            }
        }

        private void SetMessage(string text, bool ok)
        {
            _messageText = text ?? string.Empty;
            _messageOk = ok;
        }

        // ── 队列拖动 ─────────────────────────────────────────────────────────

        /// <summary>开始拖动队列第 <paramref name="index"/> 行。</summary>
        public void BeginQueueDrag(int index)
        {
            _dragFrom = index >= 0 && index < QueueRowVisibleCount ? index : -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].Row.EnableInClassList("rq-row-drag", i == _dragFrom);
            }
        }

        /// <summary>放到第 <paramref name="index"/> 行（不合法的顺序被拒并写原因）。</summary>
        public bool DropQueueAt(int index)
        {
            int from = _dragFrom;
            _dragFrom = -1;
            foreach (RowView r in _rows)
            {
                r.Row.RemoveFromClassList("rq-row-drag");
            }
            CampaignState state = CampaignSession.Current;
            if (from < 0 || state == null || index < 0 || index >= QueueRowVisibleCount || index == from)
            {
                return false;
            }
            string id = _rows[from].NodeId;
            bool ok = ResearchService.TryMove(state, id, index, out string reason);
            if (!ok)
            {
                SetMessage(GameText.Format("research.panel.message_refused", reason), false);
            }
            _lastKey = null;
            Refresh();
            return ok;
        }

        private int RowAt(Vector2 worldPos)
        {
            for (int i = 0; i < QueueRowVisibleCount && i < _rows.Count; i++)
            {
                if (_rows[i].Row.worldBound.Contains(worldPos))
                {
                    return i;
                }
            }
            return -1;
        }

        // ── 刷新 ─────────────────────────────────────────────────────────────

        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            double now = Clock();
            var h = new HashCode();
            h.Add(ResearchService.Revision);
            h.Add(ResearchCatalog.Revision);
            h.Add((int)GameText.Language);
            h.Add(GameSettings.Revision);
            h.Add(state);
            h.Add(state?.TechData ?? 0);
            h.Add(state?.UnlockedContentIds?.Length ?? 0);
            h.Add(_searchText);
            h.Add(_filter);
            h.Add(_selectedId);
            h.Add(_hoverId);
            h.Add(_messageText);
            h.Add(_nodeTemplate != null);
            h.Add(_rowTemplate != null);
            int key = h.ToHashCode();
            if (key == _lastKey && now < _nextReread)
            {
                return;
            }
            _lastKey = key;
            _nextReread = now + 0.5;
            RefreshCount++;

            EnsureViews();
            Layout();

            _title.text = GameText.Get("research.panel.title");
            _close.text = GameText.Get("research.panel.close");
            _zoomIn.text = GameText.Get("research.panel.zoom_in");
            _zoomOut.text = GameText.Get("research.panel.zoom_out");
            _zoomReset.text = GameText.Get("research.panel.zoom_reset");
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("research.panel.footer"));
            _summary.text = state != null ? ResearchService.SummaryLine(state) : string.Empty;
            ResearchStatus status = state != null ? ResearchService.StatusOf(state) : ResearchStatus.QueueEmpty;
            _status.text = state != null ? ResearchService.StatusText(state) : string.Empty;
            _status.EnableInClassList("rt-status-warn", status != ResearchStatus.Running && status != ResearchStatus.QueueEmpty);
            _message.text = _messageText;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_messageText));
            _message.EnableInClassList("rt-message-ok", _messageOk);

            RefreshFilters(state);
            RefreshNodes(state);
            RefreshQueue(state);
            ShowDetail(state, _hoverId ?? _selectedId);
            ApplyTransform();
        }

        private void EnsureViews()
        {
            if (_nodeTemplate == null || _canvas == null || _nodeViews.Count == ResearchCatalog.Nodes.Count && _nodeViews.Count > 0)
            {
                return;
            }
            foreach (NodeView v in _nodeViews)
            {
                v.Root.RemoveFromHierarchy();
            }
            foreach (Label l in _laneLabels)
            {
                l.RemoveFromHierarchy();
            }
            _nodeViews.Clear();
            _nodeById.Clear();
            _laneLabels.Clear();
            foreach (ResearchBranchDef b in ResearchCatalog.Branches)
            {
                var lane = new Label { name = "ResearchLane_" + b.Id, pickingMode = PickingMode.Ignore };
                lane.AddToClassList("rt-lane");
                _canvas.Add(lane);
                _laneLabels.Add(lane);
            }
            foreach (ResearchNodeDef n in ResearchCatalog.Nodes)
            {
                TemplateContainer root = _nodeTemplate.Instantiate();
                // TemplateContainer 才是画布里的子项：定位类 rt-node 加在它身上（ui:Instance flex 塌陷坑），里面的按钮 rt-node-btn 撑满。
                root.AddToClassList("rt-node");
                root.pickingMode = PickingMode.Ignore;
                var v = new NodeView
                {
                    Def = n,
                    Root = root,
                    Button = root.Q<Button>("RtNode"),
                    Stripe = root.Q<VisualElement>("RtNodeStripe"),
                    Fill = root.Q<VisualElement>("RtNodeBarFill"),
                    Name = root.Q<Label>("RtNodeName"),
                    State = root.Q<Label>("RtNodeState"),
                };
                v.Button.name = "RtNode_" + n.Id;
                v.Stripe.style.backgroundColor = ResearchText.BranchColor(n.Branch);
                string id = n.Id;
                v.Button.clicked += () => ClickNode(id);
                v.Button.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button == 1)
                    {
                        RightClickNode(id);
                        e.StopPropagation();
                    }
                }, TrickleDown.TrickleDown);
                v.Button.RegisterCallback<PointerEnterEvent>(_ => HoverNode(id));
                v.Button.RegisterCallback<PointerLeaveEvent>(_ =>
                {
                    if (_hoverId == id)
                    {
                        HoverNode(null);
                    }
                });
                UiTooltip.Attach(v.Button, () => NodeTooltip(id));
                _canvas.Add(root);
                _nodeViews.Add(v);
                _nodeById[id] = v;
            }
            _links.SendToBack(); // 连线画在节点下面
            _layoutKey = int.MinValue;
        }

        private TooltipContent NodeTooltip(string id)
        {
            CampaignState state = CampaignSession.Current;
            ResearchNodeDef n = ResearchCatalog.Find(id);
            return n == null ? null : new TooltipContent
            {
                Title = n.Name,
                Body = ResearchText.Detail(state, n),
                Shortcut = GameActionId.OpenResearch,
                CodexEntryId = MechanicCodex.ResearchEntryId(id),
            };
        }

        /// <summary>按筛选摆车道与节点（只在筛选 / 表 / 视图集合变化时重算，O(节点数)）。</summary>
        private void Layout()
        {
            int key = HashCode.Combine(_filter, ResearchCatalog.Revision, _nodeViews.Count, _laneLabels.Count);
            if (key == _layoutKey || _nodeViews.Count == 0)
            {
                return;
            }
            _layoutKey = key;
            LayoutCount++;
            float colW = Mathf.Max(NodeWidth + 10f, GridContent.Tuning("research.tree.col_width"));
            float rowH = Mathf.Max(NodeHeight + 6f, GridContent.Tuning("research.tree.row_height"));
            float laneGap = Mathf.Max(18f, GridContent.Tuning("research.tree.lane_gap"));
            float y = 6f;
            int maxCol = 0;
            VisibleNodeCount = 0;
            foreach (ResearchBranchDef b in ResearchCatalog.Branches)
            {
                bool show = _filter == null || _filter == b.Id;
                Label lane = _laneLabels[b.Index];
                lane.EnableInClassList("uk-hidden", !show);
                int maxRow = 0;
                foreach (ResearchNodeDef n in b.Nodes)
                {
                    maxRow = Mathf.Max(maxRow, n.Row);
                    maxCol = Mathf.Max(maxCol, n.Col);
                }
                if (show)
                {
                    lane.style.top = y;
                }
                foreach (ResearchNodeDef n in b.Nodes)
                {
                    NodeView v = _nodeById[n.Id];
                    v.Visible = show;
                    v.Root.EnableInClassList("uk-hidden", !show);
                    if (!show)
                    {
                        continue;
                    }
                    v.Pos = new Vector2(LeftMargin + n.Col * colW, y + laneGap + n.Row * rowH);
                    // 节点位置 = 表里的列 / 行 × 调参间距（数据驱动的运行时数值）。
                    v.Root.style.left = v.Pos.x;
                    v.Root.style.top = v.Pos.y;
                    VisibleNodeCount++;
                }
                if (show)
                {
                    y += laneGap + (maxRow + 1) * rowH + 8f;
                }
            }
            _canvasSize = new Vector2(LeftMargin * 2f + (maxCol + 1) * colW, y + 6f);
            _canvas.style.width = _canvasSize.x;
            _canvas.style.height = _canvasSize.y;
            _links.MarkDirtyRepaint();
            ClampPan();
        }

        private void RefreshFilters(CampaignState state)
        {
            IReadOnlyList<ResearchBranchDef> branches = ResearchCatalog.Branches;
            int want = branches.Count + 1;
            while (_filterButtons.Count < want)
            {
                int index = _filterButtons.Count;
                var b = new Button { name = "ResearchFilter" + index };
                b.AddToClassList("mw-btn");
                b.AddToClassList("rt-filter");
                b.clicked += () => SetFilter(index < _filterIds.Count ? _filterIds[index] : null);
                _filters.Add(b);
                _filterButtons.Add(b);
            }
            _filterIds.Clear();
            _filterIds.Add(null);
            Button all = _filterButtons[0];
            all.text = GameText.Get("research.panel.filter_all");
            all.EnableInClassList("rt-filter-selected", _filter == null);
            for (int i = 0; i < branches.Count; i++)
            {
                ResearchBranchDef br = branches[i];
                _filterIds.Add(br.Id);
                Button b = _filterButtons[i + 1];
                bool open = ResearchService.BranchOpen(state, br);
                b.text = open ? br.Name : br.Name + " " + GameText.Get("research.state.closed");
                b.style.borderLeftColor = ResearchText.BranchColor(br); // 分支色（数据驱动）
                b.EnableInClassList("rt-filter-selected", _filter == br.Id);
                b.EnableInClassList("rt-filter-closed", !open);
                Label lane = _laneLabels.Count > i ? _laneLabels[i] : null;
                if (lane != null)
                {
                    int done = 0;
                    foreach (ResearchNodeDef n in br.Nodes)
                    {
                        done += ResearchService.IsCompleted(state, n.Id) ? 1 : 0;
                    }
                    lane.text = open ? GameText.Format("research.branch.lane", br.Name, done, br.Nodes.Count) : GameText.Format("research.branch.closed", br.Name);
                    lane.EnableInClassList("rt-lane-closed", !open);
                }
            }
        }

        private static bool Matches(ResearchNodeDef n, string q)
        {
            if (string.IsNullOrEmpty(q))
            {
                return false;
            }
            if (Contains(n.Name, q) || Contains(n.Desc, q) || Contains(n.Branch.Name, q))
            {
                return true;
            }
            foreach (string u in n.Unlocks)
            {
                if (Contains(ResearchService.TargetName(u), q))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Contains(string s, string q) => !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        private void RefreshNodes(CampaignState state)
        {
            bool searching = _searchText.Length > 0;
            MatchCount = 0;
            FirstMatchId = null;
            foreach (NodeView v in _nodeViews)
            {
                ResearchNodeDef n = v.Def;
                ResearchNodeState s = ResearchService.StateOf(state, n);
                v.Name.text = n.Name;
                v.State.text = ResearchService.StateText(state, n);
                int inv = ResearchService.Invested(state, n.Id);
                v.Fill.style.width = new Length(Mathf.Clamp01(inv / (float)Mathf.Max(1, n.Cost)) * 100f, LengthUnit.Percent);
                v.Root.EnableInClassList("rt-node-done", s == ResearchNodeState.Done);
                v.Root.EnableInClassList("rt-node-researching", s == ResearchNodeState.Researching);
                v.Root.EnableInClassList("rt-node-queued", s == ResearchNodeState.Queued);
                v.Root.EnableInClassList("rt-node-waiting", s == ResearchNodeState.WaitingKey);
                v.Root.EnableInClassList("rt-node-available", s == ResearchNodeState.Available);
                v.Root.EnableInClassList("rt-node-locked", s == ResearchNodeState.Locked || s == ResearchNodeState.Closed);
                v.Root.EnableInClassList("rt-node-later", s == ResearchNodeState.Later);
                v.Root.EnableInClassList("rt-node-selected", n.Id == _selectedId);
                v.Match = searching && Matches(n, _searchText);
                v.Root.EnableInClassList("rt-node-match", v.Match);
                v.Root.EnableInClassList("rt-node-dim", searching && !v.Match);
                if (v.Match && v.Visible)
                {
                    MatchCount++;
                    FirstMatchId ??= n.Id;
                }
            }
            _searchCount.text = !searching ? string.Empty
                : MatchCount > 0 ? GameText.Format("research.panel.search_count", MatchCount) : GameText.Format("research.panel.search_none", _searchText);
            _links.MarkDirtyRepaint();
        }

        private void RefreshQueue(CampaignState state)
        {
            int max = ResearchService.QueueMax;
            if (_rowTemplate != null)
            {
                while (_rows.Count < max)
                {
                    int index = _rows.Count;
                    TemplateContainer root = _rowTemplate.Instantiate();
                    var r = new RowView
                    {
                        Root = root,
                        Row = root.Q<VisualElement>("RqRow"),
                        Text = root.Q<Label>("RqText"),
                        Up = root.Q<Button>("RqUp"),
                        Down = root.Q<Button>("RqDown"),
                        Remove = root.Q<Button>("RqRemove"),
                    };
                    r.Up.clicked += () => MoveRow(index, index - 1);
                    r.Down.clicked += () => MoveRow(index, index + 1);
                    r.Remove.clicked += () =>
                    {
                        if (index < _rows.Count && _rows[index].NodeId != null)
                        {
                            RightClickNode(_rows[index].NodeId);
                        }
                    };
                    r.Row.RegisterCallback<PointerDownEvent>(e =>
                    {
                        if (e.button == 0 && !(e.target is Button))
                        {
                            BeginQueueDrag(index);
                            _dragPointer = e.pointerId;
                            r.Row.CapturePointer(e.pointerId);
                        }
                    });
                    r.Row.RegisterCallback<PointerUpEvent>(e =>
                    {
                        if (_dragFrom < 0 || e.pointerId != _dragPointer)
                        {
                            return;
                        }
                        if (r.Row.HasPointerCapture(e.pointerId))
                        {
                            r.Row.ReleasePointer(e.pointerId);
                        }
                        int at = RowAt(e.position);
                        if (at < 0 || at == _dragFrom)
                        {
                            BeginQueueDrag(-1);
                        }
                        else
                        {
                            DropQueueAt(at);
                        }
                    });
                    UiTooltip.Attach(r.Row, () => new TooltipContent { Title = r.Text.text, Body = GameText.Get("research.panel.queue_tip") });
                    _queueList.Add(root);
                    _rows.Add(r);
                }
            }
            IReadOnlyList<string> q = state != null ? ResearchService.Queue(state) : Array.Empty<string>();
            _queueTitle.text = GameText.Format("research.panel.queue_title", q.Count, max);
            _queueEmpty.text = GameText.Format("research.panel.queue_empty", max);
            _queueEmpty.EnableInClassList("uk-hidden", q.Count > 0);
            _queueTip.text = GameText.Get("research.panel.queue_tip");
            QueueRowVisibleCount = Mathf.Min(q.Count, _rows.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                RowView r = _rows[i];
                bool show = i < q.Count;
                r.Root.EnableInClassList("uk-hidden", !show);
                r.NodeId = show ? q[i] : null;
                if (!show)
                {
                    continue;
                }
                ResearchNodeDef n = ResearchCatalog.Find(q[i]);
                ResearchNodeState s = ResearchService.StateOf(state, n);
                string suffix = s == ResearchNodeState.WaitingKey ? GameText.Get("research.panel.queue_item_wait")
                    : s == ResearchNodeState.Queued && n != null && !ResearchService.PrereqsDone(state, n) ? GameText.Get("research.panel.queue_item_prereq") : string.Empty;
                r.Text.text = GameText.Format("research.panel.queue_item", i + 1, n?.Name ?? q[i], ResearchService.Invested(state, q[i]), n?.Cost ?? 0, suffix);
                r.Row.EnableInClassList("rq-row-wait", s == ResearchNodeState.WaitingKey);
                r.Up.text = GameText.Get("research.panel.queue_up");
                r.Down.text = GameText.Get("research.panel.queue_down");
                r.Remove.text = GameText.Get("research.panel.queue_remove");
                r.Up.SetEnabled(i > 0);
                r.Down.SetEnabled(i < q.Count - 1);
            }
        }

        private void MoveRow(int from, int to)
        {
            BeginQueueDrag(from);
            DropQueueAt(to);
        }

        private void ShowDetail(CampaignState state, string nodeId)
        {
            ResearchNodeDef n = ResearchCatalog.Find(nodeId);
            DetailNodeId = n?.Id;
            if (_detailTitle == null)
            {
                return;
            }
            if (n == null)
            {
                _detailTitle.text = string.Empty;
                _detailNote.text = string.Empty;
                _detailGlyph.text = string.Empty;
                _detailBody.text = GameText.Get("research.panel.detail_none");
                _detailIcon.style.backgroundColor = new Color(0.25f, 0.28f, 0.3f);
                _footprint.EnableInClassList("uk-hidden", true);
                _detailAction.EnableInClassList("uk-hidden", true);
                return;
            }
            // 占位图标：分支色方块 + 分支的一个字，界面写明“占位图标”（B22，美术阶段换正式图标）。
            _detailIcon.style.backgroundColor = ResearchText.BranchColor(n.Branch);
            _detailGlyph.text = n.Branch.Glyph;
            _detailTitle.text = n.Name;
            _detailNote.text = ResearchText.KindName(n) + " · " + GameText.Get("research.detail.icon_placeholder");
            _detailBody.text = ResearchText.Detail(state, n);
            ResearchUnlockLine? building = null;
            foreach (ResearchUnlockLine l in ResearchText.UnlockLines(state, n))
            {
                if (l.FootprintW > 0)
                {
                    building = l;
                    break;
                }
            }
            _footprint.EnableInClassList("uk-hidden", building == null);
            if (building != null)
            {
                // 占地预览：按格数等比画一个框（数据驱动的尺寸）。
                _footprintBox.style.width = building.Value.FootprintW * 9f;
                _footprintBox.style.height = building.Value.FootprintH * 9f;
                _footprintText.text = building.Value.FootprintW + "×" + building.Value.FootprintH;
            }
            bool queued = state != null && ResearchService.QueueIndex(state, n.Id) >= 0;
            ResearchNodeState s = ResearchService.StateOf(state, n);
            _detailAction.EnableInClassList("uk-hidden", s == ResearchNodeState.Done);
            _detailAction.text = GameText.Get(queued ? "research.panel.dequeue" : "research.panel.enqueue");
            _detailAction.SetEnabled(queued || s == ResearchNodeState.Available || s == ResearchNodeState.Locked);
        }

        private void DrawLinks(MeshGenerationContext ctx)
        {
            LinkCount = 0;
            if (_nodeViews.Count == 0)
            {
                return;
            }
            Painter2D p = ctx.painter2D;
            p.lineWidth = 2f;
            CampaignState state = CampaignSession.Current;
            foreach (NodeView v in _nodeViews)
            {
                if (!v.Visible)
                {
                    continue;
                }
                foreach (string pre in v.Def.Prereqs)
                {
                    if (!_nodeById.TryGetValue(pre, out NodeView from) || !from.Visible)
                    {
                        continue;
                    }
                    bool done = ResearchService.IsCompleted(state, pre);
                    p.strokeColor = done ? new Color(0.45f, 0.75f, 0.5f, 0.9f) : new Color(0.55f, 0.62f, 0.68f, 0.7f);
                    Vector2 a = from.Pos + new Vector2(NodeWidth, NodeHeight * 0.5f);
                    Vector2 b = v.Pos + new Vector2(0f, NodeHeight * 0.5f);
                    float mid = (a.x + b.x) * 0.5f;
                    p.BeginPath();
                    p.MoveTo(a);
                    p.BezierCurveTo(new Vector2(mid, a.y), new Vector2(mid, b.y), b);
                    p.Stroke();
                    LinkCount++;
                }
            }
        }
    }
}
