using System;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.Settings;
using GameLogic.Core;

using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-GEN-01（FGR-GEN-081 小地图）：右下角 HUD，显示镜头附近 minimap.radius_cells 格的区域——与战略地图同一套底图、图标与筛选
    /// （<see cref="WorldMapFilters"/>）、镜头视野框；左键点哪镜头飞到哪（0.5 秒过渡）。只在观察星球表面、战略地图没开时显示。
    /// 每帧 O(图标数)；模型按真实时间 4 次 / 秒刷新；底图只在镜头移动超过一个像素格或探索变化时重画（Burst 工作线程）。
    /// </summary>
    public sealed class MinimapHudUIToolkit : UiKitPanelHost
    {
        public const int Order = 4;
        private const float ModelRefreshSeconds = 0.25f;

        public static MinimapHudUIToolkit Instance { get; private set; }

        private VisualElement _root;
        private Label _title;
        private VisualElement _canvas;
        private VisualElement _textureLayer;
        private VisualElement _vector;
        private WorldMapIconPool _icons;
        private readonly WorldMapModel _model = new WorldMapModel();
        private WorldMapTexture _texture;
        private WorldMapView _view;
        private float _nextModel;
        private readonly Vector2[] _cameraQuad = new Vector2[4];
        private bool _hasQuad;

        protected override string UxmlLocation => "Minimap";
        protected override int SortingOrder => Order;

        public bool Visible => _root != null && !_root.ClassListContains("uk-hidden");
        public WorldMapView View => _view;
        public WorldMapModel Model => _model;
        public WorldMapTexture MapTexture => _texture;
        public int VisibleIconCount => _icons?.VisibleCount ?? 0;
        public VisualElement Canvas => _canvas;
        public static int FlyCount { get; private set; }

        /// <summary>自检：编辑模式下没有载入的地点也显示。</summary>
        public static bool InWorldOverrideForTests;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            _texture?.Dispose();
            _texture = null;
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        protected override void OnReady(VisualElement root) => BindView(root);

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("MinimapRoot");
            _title = root.Q<Label>("MinimapTitle");
            _canvas = root.Q<VisualElement>("MinimapCanvas");
            _textureLayer = root.Q<VisualElement>("MinimapTexture");
            _vector = root.Q<VisualElement>("MinimapVector");
            _icons = new WorldMapIconPool(root.Q<VisualElement>("MinimapIcons"), labels: false);
            _texture ??= new WorldMapTexture(Mathf.Max(32, (int)GridContent.Tuning("minimap.texture_size")));
            _vector.generateVisualContent += ctx => WorldMapVectorLayer.Draw(ctx, _model, _view, _hasQuad ? _cameraQuad : null);
            _canvas.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (evt.button == 0)
                {
                    ClickAt(evt.localPosition);
                    evt.StopPropagation();
                }
            });
            UiTooltip.Attach(_canvas, () => new TooltipContent { Title = GameText.Get("ui.minimap.title"), Body = InputDisplay.ExpandActionTokens(GameText.Get("ui.minimap.hint")) });
        }

        private bool ShouldShow()
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || WorldGenService.ContextFor(s) == null || StrategicMapUIToolkit.IsOpen)
            {
                return false;
            }
            if (InWorldOverrideForTests)
            {
                return true;
            }
            IWorldSite site = WorldView.ObservedSite;
            return GameRoot.AnyRegionActive && site != null && site.SurfaceKind == WorldSurfaceKind.Planet;
        }

        private void Update()
        {
            if (_root == null)
            {
                return;
            }
            bool show = ShouldShow();
            if (_root.ClassListContains("uk-hidden") == show)
            {
                _root.EnableInClassList("uk-hidden", !show);
            }
            if (show)
            {
                Tick(force: false);
            }
        }

        /// <summary>每帧（显示时）：视图跟随镜头焦点、底图、模型（节流）、图标与矢量层。自检可直接调用。</summary>
        public void Tick(bool force)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }
            _title.text = GameText.Get("ui.minimap.title");
            GridCell focus = WorldView.PlanetFocusCell;
            Rect r = _canvas.contentRect;
            _view.CanvasWidth = r.width > 1f ? r.width : 172f;
            _view.CanvasHeight = r.height > 1f ? r.height : 172f;
            double half = Math.Max(16.0, GridContent.Tuning("minimap.radius_cells"));
            // 底图以“像素格”对齐的中心重画（镜头小幅移动不重画），显示时按当前焦点平移。
            double cpp = 2.0 * half / Math.Max(1f, _view.CanvasWidth);
            _view.CenterX = focus.X;
            _view.CenterY = focus.Y;
            _view.HalfWidth = half;
            WorldMapView paintView = _view;
            paintView.CenterX = Math.Round(focus.X / (cpp * 8)) * cpp * 8;
            paintView.CenterY = Math.Round(focus.Y / (cpp * 8)) * cpp * 8;
            paintView.HalfWidth = half * 1.15;
            _texture.Tick(state, paintView, force || !_texture.Painted);
            PlaceTexture();
            float now = Time.realtimeSinceStartup;
            if (force || now >= _nextModel)
            {
                _nextModel = now + ModelRefreshSeconds;
                _hasQuad = WorldMapVectorLayer.CameraGroundQuad(WorldView.Camera, _cameraQuad);
                _model.Collect(state, _view, 48);
                _icons.Layout(_model, _view, null);
                _vector.MarkDirtyRepaint();
            }
        }

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

        /// <summary>左键：镜头飞到小地图上这一点（与战略地图同一入口 WorldView.FlyTo，0.5 秒过渡）。</summary>
        public void ClickAt(Vector2 canvasPoint)
        {
            Vector2 cell = _view.ToCell(canvasPoint);
            IWorldSite home = WorldSimulation.Home;
            if (WorldView.FlyTo(home != null ? home.SiteId : HomeValleyLayout.RegionId, cell))
            {
                FlyCount++;
            }
        }
    }
}
