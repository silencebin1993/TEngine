using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.View;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-GEN-01（FGR-GEN-080 连续缩放的战略地图；FG17 第 4 节“战略地图支持玩家自定义标记和备注”；DEBT-FG0ARCH01-08）：战略地图（UI Toolkit）。
    ///
    /// - 打开：战略视角已经缩到最远（camera.zoom_max_ortho）后再拉远（<see cref="CameraDirector.ZoomOutOverflowEvent"/>），或按地图键（默认 M）。
    ///   打开时以镜头焦点为中心，比例（每个屏幕像素多少格）= 镜头最远缩放时的比例 × map.open_view_scale（约多拉远一格滚轮）；
    ///   地图最近的比例 = 镜头最远时的比例，再拉近就关掉回到镜头——从镜头到地图的比例是连续的（没有镜头的编辑模式自检用 map.cells_per_pixel_min 兜底）。
    /// - 底图：已探索区域的地形与污染（生成器采样，Burst 工作线程画）；未探索是迷雾（暗色棋盘纹）。
    /// - 矢量层：阵营领地圈（按幕显示）与危害带、信号覆盖圈（断开的红色）、突袭路线（已探索区域里的）、镜头视野框。
    /// - 图标（按类型筛选，筛选与小地图共用）：归还核心、己方建筑群 / 前哨站（<see cref="WorldMapOwnClusters"/>）、据点（已探索）、遗迹点（已探索）、
    ///   起始区资源点、突袭（已探索区域里的）、玩家标记；
    ///   远征队目前是独立地点（Demo 远征，DEBT-FG0ARCH01-01），任务点属于 FG8——见 DEBT-FG3GEN01-06。
    /// - 操作：滚轮缩放（以光标为中心）、左键拖动平移、左键单击飞过去（镜头过渡 camera.fly_seconds = 0.5 秒）并关地图、点标记选中编辑备注、
    ///   右键加标记（上限 map.marker_max），Esc / 地图键 / 关闭按钮关掉。
    /// 模态（地图盖住世界，但世界照常运行、不暂停）。开着时每帧 O(图标数)（上限 map.max_icons），模型按真实时间 5 次 / 秒刷新；关着时 O(1)。
    /// </summary>
    public sealed class StrategicMapUIToolkit : UiKitPanelHost
    {
        public const int Order = 30050;
        private const float ModelRefreshSeconds = 0.2f;
        private const float DragThresholdPx = 5f;

        public static StrategicMapUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        private VisualElement _root;
        private Label _title;
        private Label _scale;
        private Button _close;
        private readonly Button[] _filters = new Button[8];
        private VisualElement _canvas;
        private VisualElement _textureLayer;
        private VisualElement _vector;
        private VisualElement _iconLayer;
        private Label _iconCount;
        private VisualElement _markerEditor;
        private Label _markerTitle;
        private TextField _markerNote;
        private Button _markerDelete;
        private Label _feedback;
        private Label _hint;

        private WorldMapIconPool _icons;
        private readonly WorldMapModel _model = new WorldMapModel();
        private WorldMapTexture _texture;
        private WorldMapView _view;
        private float _nextModel;
        private int _modelKey;
        private string _selectedMarker;
        private readonly Vector2[] _cameraQuad = new Vector2[4];
        private readonly Vector2[] _farCameraQuad = new Vector2[4];
        private bool _hasCameraQuad;
        /// <summary>本次打开时算出的“地图最近”比例（每个画布像素多少格）= 镜头最远缩放时的比例；0 = 没有镜头，用表里的兜底值。</summary>
        private double _minCellsPerCanvasPixel;

        private bool _pointerDown;
        private int _pointerButton;
        private Vector2 _pointerStart;
        private bool _dragging;
        private double _dragCenterX;
        private double _dragCenterY;

        protected override string UxmlLocation => "StrategicMap";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public WorldMapView View => _view;
        public WorldMapModel Model => _model;
        public WorldMapTexture MapTexture => _texture;
        public int VisibleIconCount => _icons?.VisibleCount ?? 0;
        public VisualElement IconAt(int i) => _icons?.IconAt(i);
        public string SelectedMarker => _selectedMarker;
        public string FeedbackText => _feedback?.text ?? string.Empty;
        public Button FilterButton(WorldMapLayer layer) => _filters[(int)layer];
        public VisualElement Canvas => _canvas;
        public TextField MarkerNoteField => _markerNote;
        public Button MarkerDeleteButton => _markerDelete;
        public static int OpenCount { get; private set; }
        public static int FlyCount { get; private set; }

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private void Awake()
        {
            Instance = this;
            GameEvent.AddEventListener(CameraDirector.ZoomOutOverflowEvent, OnZoomOverflow);
        }

        protected override void OnDestroy()
        {
            GameEvent.RemoveEventListener(CameraDirector.ZoomOutOverflowEvent, OnZoomOverflow);
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            _texture?.Dispose();
            _texture = null;
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        private void OnZoomOverflow()
        {
            if (!IsOpen && CanOpen())
            {
                Open();
            }
        }

        public static void Open()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                return;
            }
            Instance.SetOpen(true);
        }

        private static bool _hasPendingCenter;
        private static double _pendingCenterX;
        private static double _pendingCenterY;

        /// <summary>FG5-RND-05（情报面板“在地图上查看”）：打开地图并以 (<paramref name="x"/>, <paramref name="y"/>)（格）为中心；已开着就移过去。
        /// 视野至少放得下 <paramref name="minHalfWidth"/> 格（突袭预报箭头整支都看得到）。</summary>
        public static void OpenCentered(double x, double y, double minHalfWidth)
        {
            _hasPendingCenter = true;
            _pendingCenterX = x;
            _pendingCenterY = y;
            _pendingMinHalf = minHalfWidth;
            if (IsOpen && Instance != null)
            {
                Instance.ApplyPendingCenter();
                return;
            }
            Open();
        }

        private static double _pendingMinHalf;

        private void ApplyPendingCenter()
        {
            if (!_hasPendingCenter)
            {
                return;
            }
            _hasPendingCenter = false;
            _view.CenterX = _pendingCenterX;
            _view.CenterY = _pendingCenterY;
            _view.HalfWidth = Math.Max(_view.HalfWidth, _pendingMinHalf);
            ClampZoom();
            _modelKey = 0;
        }

        /// <summary>自检读点：当前视图中心与半宽（格）。</summary>
        public double ViewCenterX => _view.CenterX;
        public double ViewCenterY => _view.CenterY;
        public double ViewHalfWidth => _view.HalfWidth;

        public static void Close()
        {
            _pendingOpen = false;
            _hasPendingCenter = false;
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
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SetOpen(true);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("MapRoot");
            _title = root.Q<Label>("MapTitle");
            _scale = root.Q<Label>("MapScale");
            _close = root.Q<Button>("MapClose");
            for (int i = 0; i < _filters.Length; i++)
            {
                _filters[i] = root.Q<Button>("MapFilter" + i.ToString(CultureInfo.InvariantCulture));
                WorldMapLayer layer = (WorldMapLayer)i;
                _filters[i].clicked += () => ToggleFilter(layer);
            }
            _canvas = root.Q<VisualElement>("MapCanvas");
            _textureLayer = root.Q<VisualElement>("MapTexture");
            _vector = root.Q<VisualElement>("MapVector");
            _iconLayer = root.Q<VisualElement>("MapIcons");
            _iconCount = root.Q<Label>("MapIconCount");
            _markerEditor = root.Q<VisualElement>("MarkerEditor");
            _markerTitle = root.Q<Label>("MarkerTitle");
            _markerNote = root.Q<TextField>("MarkerNote");
            _markerNote.maxLength = WorldMapMarkers.NoteMaxLength; // 输入上限与存档一致（超长部分不会“打了却存不进去”）
            _markerDelete = root.Q<Button>("MarkerDelete");
            _feedback = root.Q<Label>("MapFeedback");
            _hint = root.Q<Label>("MapHint");
            _icons = new WorldMapIconPool(_iconLayer, labels: true);
            _texture ??= new WorldMapTexture(Mathf.Max(64, (int)GridContent.Tuning("map.texture_size")));
            _close.clicked += () => SetOpen(false);
            _markerDelete.clicked += DeleteSelectedMarker;
            _markerNote.RegisterValueChangedCallback(evt => SetSelectedMarkerNote(evt.newValue));
            _vector.generateVisualContent += ctx => WorldMapVectorLayer.Draw(ctx, _model, _view, _hasCameraQuad ? _cameraQuad : null);
            _canvas.RegisterCallback<WheelEvent>(OnWheel);
            _canvas.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _canvas.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _canvas.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _canvas.RegisterCallback<GeometryChangedEvent>(_ => SyncCanvasSize());
        }

        /// <summary>有没有可以显示的星球世界（战役在跑、生成器世界）。</summary>
        private static bool CanOpen()
        {
            CampaignState s = CampaignSession.Current;
            return HasWorldMap(s) && (GameRoot.AnyRegionActive || InWorldOverrideForTests);
        }

        /// <summary>这份战役有没有战略地图：生成器世界（v1 起）有；旧版本原型地形（生成器版本 0）没有——地图键回退打开任务日志（兼战役地图）。</summary>
        public static bool HasWorldMap(CampaignState state) => state != null && WorldGenService.ContextFor(state) != null;

        public void SetOpen(bool open)
        {
            if (_root == null || open == IsOpen)
            {
                return;
            }
            if (open && !CanOpen())
            {
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                OpenCount++;
                GuidanceHooks.Raise(GuidanceHooks.StrategicMapFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _selectedMarker = null;
                _feedback.text = string.Empty;
                CenterOnCamera();
                ApplyPendingCenter();
                _modelKey = 0;
                _nextModel = 0f;
                RefreshTexts();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                _pointerDown = false;
                _dragging = false;
            }
        }

        /// <summary>
        /// 以镜头焦点为中心打开（FGR-GEN-080 连续缩放）：地图最近的比例 = 镜头最远缩放时每个屏幕像素看到的格数（换算到画布像素），
        /// 打开时再拉远 map.open_view_scale 倍（约一格滚轮）。于是镜头最远 → 再拉远一下 → 地图，地图里拉近一下 → 回到最远的镜头，
        /// 两边的比例首尾相接，不会跳。
        /// </summary>
        private void CenterOnCamera()
        {
            GridCell c = WorldView.PlanetFocusCell;
            SyncCanvasSize();
            WorldVectorQuad(out _);
            _minCellsPerCanvasPixel = CameraFarCellsPerCanvasPixel();
            _view.CenterX = c.X;
            _view.CenterY = c.Y;
            _view.HalfWidth = MinHalfWidth * Math.Max(1.0, GridContent.Tuning("map.open_view_scale"));
            ClampZoom();
        }

        /// <summary>
        /// 镜头最远缩放时每个画布像素对应多少格，正交与透视都按最远视口射线与地面的交点计算。
        /// 屏幕像素 → 画布像素按面板根节点宽度换算；算不出时返回 0，使用表中兜底值。
        /// </summary>
        private double CameraFarCellsPerCanvasPixel()
        {
            Camera cam = WorldView.Camera;
            if (!_hasCameraQuad || cam == null || cam.orthographicSize <= 0.01f || cam.pixelWidth < 1
                || !WorldMapVectorLayer.CameraGroundQuad(cam, _farCameraQuad, CameraDirector.MaxStrategyOrthographicSize))
            {
                return 0.0;
            }
            float panelWidth = _canvas?.panel?.visualTree?.layout.width ?? 0f;
            if (!(panelWidth > 1f))
            {
                return 0.0;
            }
            double farWidth = Vector2.Distance(_farCameraQuad[0], _farCameraQuad[1]);
            double cellsPerScreenPixel = farWidth / cam.pixelWidth;
            double screenPixelsPerCanvasPixel = cam.pixelWidth / (double)panelWidth;
            return cellsPerScreenPixel * screenPixelsPerCanvasPixel;
        }

        /// <summary>自检 / 冒烟读点：本次打开时的“地图最近”比例（每个画布像素多少格），0 = 用表里的兜底值。</summary>
        public double MinCellsPerCanvasPixel => _minCellsPerCanvasPixel;

        private bool WorldVectorQuad(out float halfWidth)
        {
            halfWidth = 0f;
            _hasCameraQuad = WorldMapVectorLayer.CameraGroundQuad(WorldView.Camera, _cameraQuad);
            if (!_hasCameraQuad)
            {
                return false;
            }
            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (Vector2 p in _cameraQuad)
            {
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
            }
            halfWidth = (maxX - minX) * 0.5f;
            return halfWidth > 0f;
        }

        private void SyncCanvasSize()
        {
            if (_canvas == null)
            {
                return;
            }
            Rect r = _canvas.contentRect;
            if (r.width > 1f && r.height > 1f)
            {
                // 画布尺寸变了（第一次打开时布局晚一帧、改分辨率）：按比例调半宽，保持“每个画布像素多少格”不变——比例不因布局而跳。
                if (_view.CanvasWidth > 1f && Math.Abs(r.width - _view.CanvasWidth) > 0.5f)
                {
                    _view.HalfWidth *= r.width / _view.CanvasWidth;
                }
                _view.CanvasWidth = r.width;
                _view.CanvasHeight = r.height;
            }
            else if (_view.CanvasWidth < 1f)
            {
                _view.CanvasWidth = 800f;
                _view.CanvasHeight = 600f;
            }
        }

        private double MinHalfWidth => (_minCellsPerCanvasPixel > 0.0 ? _minCellsPerCanvasPixel : GridContent.Tuning("map.cells_per_pixel_min")) * Math.Max(1f, _view.CanvasWidth) * 0.5;
        private double MaxHalfWidth => GridContent.Tuning("map.cells_per_pixel_max") * Math.Max(1f, _view.CanvasWidth) * 0.5;

        private void ClampZoom()
        {
            _view.HalfWidth = Math.Max(MinHalfWidth, Math.Min(MaxHalfWidth, _view.HalfWidth));
        }

        private void RefreshTexts()
        {
            _title.text = GameText.Get("ui.map.title");
            _close.text = GameText.Get("ui.map.close");
            _markerDelete.text = GameText.Get("ui.map.marker_delete");
            _hint.text = InputDisplay.ExpandActionTokens(GameText.Get("ui.map.hint"));
            for (int i = 0; i < _filters.Length; i++)
            {
                WorldMapLayer layer = (WorldMapLayer)i;
                bool on = WorldMapFilters.IsOn(layer);
                string name = GameText.Get(WorldMapFilters.TextKey(layer));
                _filters[i].text = on ? "▸ " + name : name;
                _filters[i].EnableInClassList("wg-filter-off", !on);
            }
        }

        public void ToggleFilter(WorldMapLayer layer)
        {
            WorldMapFilters.Toggle(layer);
            _modelKey = 0;
            RefreshTexts();
        }

        private void Update()
        {
            bool inWorld = CampaignSession.Current != null && (GameRoot.AnyRegionActive || InWorldOverrideForTests);
            if (!IsOpen)
            {
                // 地图键（默认 M）在世界里打开地图；上面盖着更高层的模态面板时不在它下面开一个看不见的。
                // 旧版本原型地形的存档没有战略地图：不读地图键，留给任务日志（MissionLogUIToolkit 回退打开任务日志）。
                if (inWorld && !AnyModalAbove(Order) && HasWorldMap(CampaignSession.Current) && InputRouter.ConsumeGlobalAction(GameActionId.OpenMap))
                {
                    SetOpen(true);
                }
                return;
            }
            if (!inWorld)
            {
                SetOpen(false);
                return;
            }
            // 地图自己是模态：开着时地图键要允许在模态期间读取（上面再盖着别的面板时不关）。
            if (!AnyModalAbove(Order) && InputRouter.ConsumeGlobalAction(GameActionId.OpenMap, allowDuringModal: true))
            {
                SetOpen(false);
                return;
            }
            Tick(force: false);
        }

        /// <summary>每帧：同步画布尺寸、底图、模型（节流）、图标与矢量层。自检可直接调用（<paramref name="force"/> = 不节流）。</summary>
        public void Tick(bool force)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }
            SyncCanvasSize();
            _texture.Tick(state, _view, force || !_texture.Painted);
            PlaceTexture();
            float now = Time.realtimeSinceStartup;
            int key = HashCode.Combine(Math.Round(_view.CenterX, 1), Math.Round(_view.CenterY, 1), Math.Round(_view.HalfWidth, 1), _view.CanvasWidth, _view.CanvasHeight,
                WorldMapFilters.Revision, HashCode.Combine(WorldMapMarkers.Revision, (int)GameText.Language, Campaign.Economy.IntelService.Revision));
            if (force || key != _modelKey || now >= _nextModel)
            {
                _modelKey = key;
                _nextModel = now + ModelRefreshSeconds;
                WorldVectorQuad(out _);
                _model.Collect(state, _view, Mathf.Max(8, (int)GridContent.Tuning("map.max_icons")));
                _icons.Layout(_model, _view, _selectedMarker);
                _vector.MarkDirtyRepaint();
                _iconCount.text = GameText.Format("ui.map.icon_count", _icons.VisibleCount);
                double cpp = _view.CellsPerCanvasPixel;
                _scale.text = GameText.Format("ui.map.scale", cpp.ToString(cpp < 10 ? "0.0" : "0", CultureInfo.InvariantCulture));
                RefreshMarkerEditor();
            }
        }

        /// <summary>底图按它画出来时的视图摆放（视图变了、新图还没画完时不错位）。</summary>
        private void PlaceTexture()
        {
            if (_texture.Texture == null || !_texture.Painted)
            {
                _textureLayer.style.backgroundImage = StyleKeyword.None;
                return;
            }
            WorldMapView p = _texture.PaintedView;
            Vector2 a = _view.ToCanvas(p.MinX, p.MaxY);
            Vector2 b = _view.ToCanvas(p.MaxX, p.MinY);
            _textureLayer.style.left = a.x;
            _textureLayer.style.top = a.y;
            _textureLayer.style.width = Mathf.Max(1f, b.x - a.x);
            _textureLayer.style.height = Mathf.Max(1f, b.y - a.y);
            _textureLayer.style.backgroundImage = new StyleBackground(_texture.Texture);
        }

        // ── 输入 ──

        private void OnWheel(WheelEvent evt)
        {
            ZoomAt(evt.localMousePosition, evt.delta.y > 0f ? 1 : -1);
            evt.StopPropagation();
        }

        /// <summary>以画布上一点为中心缩放一次（<paramref name="direction"/> &gt; 0 拉远）。拉近到最小比例以下 = 关地图回到镜头（连续缩放）。</summary>
        public void ZoomAt(Vector2 canvasPoint, int direction)
        {
            float factor = Mathf.Max(1.05f, GridContent.Tuning("map.zoom_factor"));
            Vector2 anchor = _view.ToCell(canvasPoint);
            double before = _view.HalfWidth;
            double target = direction > 0 ? before * factor : before / factor;
            if (direction < 0 && target < MinHalfWidth - 1e-6)
            {
                SetOpen(false); // 拉近到底：回到镜头（镜头仍在最远缩放，衔接连续）。
                return;
            }
            _view.HalfWidth = target;
            ClampZoom();
            double k = _view.HalfWidth / before;
            _view.CenterX = anchor.x + (_view.CenterX - anchor.x) * k;
            _view.CenterY = anchor.y + (_view.CenterY - anchor.y) * k;
            _modelKey = 0;
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            _pointerDown = true;
            _pointerButton = evt.button;
            _pointerStart = evt.localPosition;
            _dragging = false;
            _dragCenterX = _view.CenterX;
            _dragCenterY = _view.CenterY;
            _canvas.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_pointerDown || _pointerButton != 0)
            {
                return;
            }
            Vector2 d = (Vector2)evt.localPosition - _pointerStart;
            if (!_dragging && d.magnitude < DragThresholdPx)
            {
                return;
            }
            _dragging = true;
            double cpp = _view.CellsPerCanvasPixel;
            _view.CenterX = _dragCenterX - d.x * cpp;
            _view.CenterY = _dragCenterY + d.y * cpp;
            _modelKey = 0;
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!_pointerDown)
            {
                return;
            }
            _pointerDown = false;
            if (_canvas.HasPointerCapture(evt.pointerId))
            {
                _canvas.ReleasePointer(evt.pointerId);
            }
            if (_dragging)
            {
                _dragging = false;
                return;
            }
            if (evt.button == 1)
            {
                AddMarkerAt(evt.localPosition);
            }
            else if (evt.button == 0)
            {
                ClickAt(evt.localPosition);
            }
            evt.StopPropagation();
        }

        /// <summary>左键单击：点在标记上 = 选中编辑备注；否则镜头飞过去（0.5 秒）并关地图。自检与指针同一入口。</summary>
        public void ClickAt(Vector2 canvasPoint)
        {
            string marker = MarkerNear(canvasPoint, 10f);
            if (marker != null)
            {
                _selectedMarker = marker;
                _modelKey = 0;
                RefreshMarkerEditor();
                return;
            }
            Vector2 cell = _view.ToCell(canvasPoint);
            IWorldSite home = WorldSimulation.Home;
            string site = home != null ? home.SiteId : HomeValleyLayout.RegionId;
            if (WorldView.FlyTo(site, cell))
            {
                FlyCount++;
            }
            SetOpen(false);
        }

        /// <summary>右键：在这一点加一个玩家标记并选中（上限时说明原因）。</summary>
        public MapMarkerRecord AddMarkerAt(Vector2 canvasPoint)
        {
            Vector2 cell = _view.ToCell(canvasPoint);
            MapMarkerRecord m = WorldMapMarkers.Add(CampaignSession.Current, WorldGenContent.EarthSurfaceId, Mathf.RoundToInt(cell.x), Mathf.RoundToInt(cell.y));
            if (m == null)
            {
                _feedback.text = GameText.Format("ui.map.marker_limit", WorldMapMarkers.Limit);
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied); // FG00 B07：拒绝音 + 原因文字
                return null;
            }
            Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck);
            GuidanceHooks.Raise(GuidanceHooks.MapMarkerFirstAdded);
            _selectedMarker = m.MarkerId;
            _feedback.text = GameText.Format("ui.map.marker_added", GameText.Format("ui.map.marker_default", m.Serial));
            _modelKey = 0;
            RefreshMarkerEditor();
            return m;
        }

        private string MarkerNear(Vector2 canvasPoint, float radius)
        {
            string best = null;
            float bestD = radius;
            foreach (WorldMapItem item in _model.Items)
            {
                if (item.Kind != WorldMapItemKind.Marker)
                {
                    continue;
                }
                float d = Vector2.Distance(_view.ToCanvas(item.X, item.Y), canvasPoint);
                if (d <= bestD)
                {
                    bestD = d;
                    best = item.Id;
                }
            }
            return best;
        }

        /// <summary>改选中标记的备注（备注输入框的值变化回调与自检同一入口）。</summary>
        public void SetSelectedMarkerNote(string note)
        {
            if (_selectedMarker != null)
            {
                WorldMapMarkers.SetNote(CampaignSession.Current, _selectedMarker, note);
                _modelKey = 0;
            }
        }

        public bool MarkerEditorVisible => _markerEditor != null && !_markerEditor.ClassListContains("uk-hidden");

        public void DeleteSelectedMarker()
        {
            if (_selectedMarker != null && WorldMapMarkers.Remove(CampaignSession.Current, _selectedMarker))
            {
                _selectedMarker = null;
                _modelKey = 0;
                RefreshMarkerEditor();
            }
        }

        private void RefreshMarkerEditor()
        {
            MapMarkerRecord m = _selectedMarker != null ? WorldMapMarkers.Find(CampaignSession.Current, _selectedMarker) : null;
            if (m == null)
            {
                _selectedMarker = null;
                _markerEditor.EnableInClassList("uk-hidden", true);
                return;
            }
            _markerEditor.EnableInClassList("uk-hidden", false);
            _markerTitle.text = GameText.Format("ui.map.marker_default", m.Serial) + " · " + GameText.Get("ui.map.marker_note");
            // 只在输入框的内容与存档“规范形式不同”时回写（换了选中标记、被别处改了）：正在输入的“iron ”规范后就是存档里的“iron”，
            // 不回写——否则末尾空格每帧被删掉，多词备注打不出来（B16 英文界面）。
            if (WorldMapMarkers.NormalizeNote(_markerNote.value) != m.Note)
            {
                _markerNote.SetValueWithoutNotify(m.Note);
            }
        }

        /// <summary>自检：把视图设到指定中心与半宽（格），画布尺寸取 <paramref name="w"/>×<paramref name="h"/>（编辑模式没有布局时用）。</summary>
        public void SetViewForTests(double cx, double cy, double halfWidth, float w, float h)
        {
            _view.CenterX = cx;
            _view.CenterY = cy;
            _view.HalfWidth = halfWidth;
            _view.CanvasWidth = w;
            _view.CanvasHeight = h;
            _modelKey = 0;
        }
    }
}
