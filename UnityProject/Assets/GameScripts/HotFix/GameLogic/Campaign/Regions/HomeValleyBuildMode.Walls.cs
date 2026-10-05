using System.Collections.Generic;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG6-DEF-02（FGR-DEF-010 屏障可拖拽建造；FG06 第 4 节“放置屏障时预览对流场的影响（敌人会改走哪里）”；DEBT-FG3LOG01-03 拖拽建墙）：建造模式里的屏障 / 闸门。
    /// - 选中屏障 / 闸门后按住左键拖拽：与传送带同一条自动转角路径，逐格着色（绿可放 / 红不可放 + 第一处不合法叠叉），状态栏写格数与总造价；松开时全有或全无地铺下（一步撤销）。
    /// - 预览线：没拖时指着的那一格、拖拽时整段，按“放下之后”算敌人的来路（Burst 距离场），画成地面上的折线；完全堵死时画到第一段新墙为止并写明敌人会攻击挡路的墙。
    ///   只在格子 / 朝向 / 建筑记录变化时重算（与放置预览同一节奏），每帧 O(1)。
    /// 表现是占位（LineRenderer 折线 + 状态栏文字，FG00 B22），在调试层与 DEBT 登记。
    /// </summary>
    public sealed partial class HomeValleyBuildMode
    {
        /// <summary>拖屏障时的规划（格子、逐格合法性、造价、敌方来路预览）；没在拖时为 null。</summary>
        public WallPlan WallPlan { get; private set; }

        /// <summary>没拖时指着的那一格放下一座屏障 / 闸门之后的敌方来路预览（选中的不是屏障 / 闸门时为 null）。</summary>
        public RoutePreview HoverRoute { get; private set; }

        private readonly WallPlan _wallBuffer = new WallPlan();
        private readonly RoutePreview _hoverRouteBuffer = new RoutePreview();
        private readonly List<GridCell> _hoverCells = new List<GridCell>(1);
        private readonly List<LineRenderer> _routeLines = new List<LineRenderer>(4);
        private Material _routeMaterial;
        private int _routeRevision = -1;
        private object _routeShown;

        /// <summary>当前画出来的敌方来路预览线条数（自检读）。</summary>
        public int ActiveRouteLineCount { get; private set; }

        private bool SelectedIsWall => SelectedTypeId != null && DefenseCatalog.IsDraggable(SelectedTypeId);

        /// <summary>松开鼠标：整段铺下（全有或全无），状态栏写放下几座、敌方来路怎么变。</summary>
        private void CommitWall(CampaignState state, GridCell from, GridCell to, WallPlan lastPlan)
        {
            string type = SelectedTypeId;
            // 复审修复（P2 性能）：松手时复用拖拽中已算好的规划（同一类型、同一起止格），不再多算一遍来路距离场；对不上时才重算。
            WallPlan plan = lastPlan != null && lastPlan.TypeId == type && lastPlan.From == from && lastPlan.To == to
                ? lastPlan
                : DefenseService.PlanWall(state, type, from, to);
            string route = plan.Ok ? plan.Route.Summary() : null;
            string cutWarning = plan.Ok ? plan.CutsOffWarning() : null;
            GridOpResult r = DefenseService.TryPlaceWall(state, type, from, to, out int placed);
            if (!r.Success)
            {
                GuidanceHooks.Raise(GuidanceHooks.FirstBlockedPlacement);
                Report(r, state);
                return;
            }
            LastResult = r;
            SetStatus(GameText.Format("build.wall.placed", placed, HomeGridService.DisplayName(type)) + (route != null ? "\n" + route : string.Empty)
                      + (cutWarning != null ? "\n" + cutWarning : string.Empty), false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
        }

        /// <summary>选中屏障 / 闸门、没在拖时：指着的那一格放下之后的敌方来路（只在放置预览重算时调用，O(1) 次 Burst 距离场）。</summary>
        private void RefreshHoverRoute(CampaignState state)
        {
            if (!SelectedIsWall || Preview == null || !Preview.Ok || state == null)
            {
                HoverRoute = null;
                return;
            }
            _hoverCells.Clear();
            _hoverCells.Add(HoverCell);
            DefenseService.PreviewRoutes(state, _hoverCells, _hoverRouteBuffer);
            HoverRoute = _hoverRouteBuffer;
            Preview.Notes.Add(_hoverRouteBuffer.Summary());
        }

        /// <summary>画敌方来路预览线（拖拽时用整段的预览，没拖时用指着那一格的预览）。只在预览对象或修订号变化时重画。</summary>
        private void RefreshRouteLines()
        {
            RoutePreview route = WallPlan != null && WallPlan.Ok ? WallPlan.Route : WallPlan == null ? HoverRoute : null;
            object key = route;
            if (ReferenceEquals(key, _routeShown) && _routeRevision == Revision)
            {
                return;
            }
            _routeShown = key;
            _routeRevision = Revision;
            int used = 0;
            if (route != null && _root != null)
            {
                foreach (List<GridCell> line in route.Routes)
                {
                    if (line.Count < 2)
                    {
                        continue;
                    }
                    LineRenderer lr = RouteLine(used++);
                    lr.positionCount = line.Count;
                    for (int i = 0; i < line.Count; i++)
                    {
                        lr.SetPosition(i, new Vector3(line[i].X, 0.45f, line[i].Y));
                    }
                    // 堵死 = 品红（另有状态栏文字“会把敌人的来路完全堵死”）；改道 = 橙色。颜色之外有文字，色盲安全（B15）。
                    Color c = route.Sealed ? new Color(0.95f, 0.2f, 0.85f, 0.95f) : new Color(1f, 0.55f, 0.1f, 0.95f);
                    lr.startColor = c;
                    lr.endColor = c;
                    lr.gameObject.SetActive(true);
                }
            }
            for (int i = used; i < _routeLines.Count; i++)
            {
                _routeLines[i].gameObject.SetActive(false);
            }
            ActiveRouteLineCount = used;
        }

        private LineRenderer RouteLine(int index)
        {
            while (_routeLines.Count <= index)
            {
                if (_routeMaterial == null)
                {
                    _routeMaterial = new Material(Shader.Find("Sprites/Default"));
                }
                var go = new GameObject("EnemyRoutePreview" + _routeLines.Count);
                go.transform.SetParent(_root.transform, false);
                LineRenderer lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = _routeMaterial;
                lr.widthMultiplier = 0.35f;
                lr.useWorldSpace = true;
                lr.numCapVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                _routeLines.Add(lr);
            }
            return _routeLines[index];
        }

        private void ReleaseRouteLines()
        {
            _routeLines.Clear(); // 线条对象随 _root 一起销毁
            if (_routeMaterial != null)
            {
                GameLogic.View.UnityObjects.Release(_routeMaterial);
                _routeMaterial = null;
            }
            _routeShown = null;
            _routeRevision = -1;
            ActiveRouteLineCount = 0;
        }

        /// <summary>拖屏障时的虚影格（逐格绿 / 红）与第一处不合法的叉。返回下一个可用的格子下标。</summary>
        private int PlaceWallTiles(int tile, ref bool showCross)
        {
            WallPlan plan = WallPlan;
            for (int i = 0; i < plan.Cells.Count && tile < 512; i++)
            {
                bool ok = plan.Ok || (i < plan.CellOk.Count && plan.CellOk[i] && plan.FirstBadIndex != i);
                PlaceTile(tile++, plan.Cells[i], ok && plan.Ok ? _okMaterial : _badMaterial, 0.35f);
            }
            if (!plan.Ok && plan.Cells.Count > 0)
            {
                showCross = true;
                GridCell bad = plan.FirstBadIndex >= 0 && plan.FirstBadIndex < plan.Cells.Count ? plan.Cells[plan.FirstBadIndex] : plan.Cells[plan.Cells.Count - 1];
                foreach (Transform bar in _cross.transform)
                {
                    bar.localScale = new Vector3(1.4f, 0.15f, 0.3f);
                }
                _cross.transform.position = new Vector3(bad.X, 0.6f, bad.Y);
            }
            return tile;
        }
    }
}
