using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG0-ARCH-04（FGR-LOG-003 原型；FGT-LOG-001 原型版本）：家园的建造模式——正式输入走通“放置、旋转、拆除、占地校验”。
    ///
    /// 输入（全部经 <see cref="InputRouter"/> 的动作与上下文，按键可重绑，FG00 B02）：
    /// - 建造菜单键（默认 B）打开 / 关闭建造模式（输入上下文切到“建造”，FGR-ARC-012）；拆除模式键（默认 X）直接进入拆除模式；
    /// - 选中建筑后虚影跟随鼠标、吸附格网；旋转键（默认 R）旋转虚影 90°；没有选中时，指着已有建筑按旋转键旋转那座建筑；
    /// - 左键放置（合法才放，非法给原因）；拆除模式下左键标记 / 取消标记拆除（关键建筑先确认）；
    /// - 右键取消当前选择 → 退出拆除模式 → 退出建造模式；Esc 退出建造模式（经 <see cref="UiEscapeStack"/>）。
    /// 战略暂停下可以继续规划，施工在恢复后推进（FG03 第 4 节）。
    ///
    /// 表现（占位，FG00 B22）：虚影逐格着色（绿 = 可放、红 = 不可放），不合法时再叠一个“叉”形（色盲安全，颜色之外有形状）；
    /// 端口用箭头（输出橙色长箭头、输入青色短箭头，形状不同）；地形叠加层按 fg.TbGridTerrain 的颜色 + 图案画出核心周围一片，
    /// 迷雾变暗、污染加斜线、核心通道加黄框。全部由格网数据推导，不另算一套（FG14 硬约束 4）。
    ///
    /// 状态只读写 <see cref="HomeGridService"/>；本类不保存任何需要进存档的东西（选择、朝向是界面状态）。
    /// 每帧开销与建筑数无关：鼠标换格 / 换朝向 / 状态变化时才重算一次 O(占地) 的校验。
    /// </summary>
    public sealed class HomeValleyBuildMode
    {
        /// <summary>当前家园的建造模式（家园未激活时为 null）。HUD 与自检读这里。</summary>
        public static HomeValleyBuildMode Current { get; private set; }

        public bool IsOpen { get; private set; }
        public bool DemolishMode { get; private set; }
        public string SelectedTypeId { get; private set; }
        public int GhostRotation { get; private set; }
        public bool HasHover { get; private set; }
        public GridCell HoverCell { get; private set; }
        public string HoverBuildingId { get; private set; }
        /// <summary>当前虚影的校验结果（没有选中建筑时为 null）。</summary>
        public GridPlacementResult Preview { get; private set; }
        /// <summary>最近一次操作的结果文字（当前语言）与是否为失败。</summary>
        public string StatusText { get; private set; } = string.Empty;
        public bool StatusIsError { get; private set; }
        /// <summary>任何可见状态变化时 +1（HUD 按它刷新，没变化的帧 O(1)）。</summary>
        public int Revision { get; private set; }
        /// <summary>最近一次操作的结果（自检断言用）。</summary>
        public GridOpResult LastResult { get; private set; }

        private readonly Action _escClose;
        private readonly List<PortPlacement> _ports = new List<PortPlacement>(4);
        private readonly List<GridCell> _cells = new List<GridCell>(32);
        private readonly GridPlacementResult _previewBuffer = new GridPlacementResult();
        private GameObject _root;
        private GameObject _overlay;
        private Texture2D _overlayTexture;
        private Material _overlayMaterial;
        private Material _okMaterial;
        private Material _badMaterial;
        private Material _markMaterial;
        private Material _outMaterial;
        private Material _inMaterial;
        private readonly List<GameObject> _tiles = new List<GameObject>(32);
        private readonly List<GameObject> _arrows = new List<GameObject>(8);
        private GameObject _cross;
        private int _previewKey = int.MinValue;
        private BuildingRecord[] _previewRecordsRef;
        private float _previewScrap = -1f;

        public HomeValleyBuildMode()
        {
            _escClose = () => Close();
        }

        public static void Bind(HomeValleyBuildMode mode)
        {
            Current = mode;
        }

        public static void Unbind(HomeValleyBuildMode mode)
        {
            if (Current == mode)
            {
                Current = null;
            }
        }

        // ── 界面 / 自检也走这些入口（与按键同一条路径）──────────────────────────────────

        public void Open()
        {
            if (IsOpen)
            {
                return;
            }
            IsOpen = true;
            DemolishMode = false;
            SelectedTypeId = null;
            InputRouter.SetBuildMode(true);
            GuidanceHooks.Raise(GuidanceHooks.BuildModeFirstOpen);
            EnsureVisuals();
            RebuildOverlay();
            SetStatus(string.Empty, false);
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }
            IsOpen = false;
            DemolishMode = false;
            SelectedTypeId = null;
            Preview = null;
            InputRouter.SetBuildMode(false);
            UiEscapeStack.Remove(this);
            DestroyVisuals();
            Revision++;
        }

        /// <summary>选中要放置的建筑（进入放置状态，退出拆除模式）。不可放置 / 未解锁的也能选中，虚影会给出原因。</summary>
        public void Select(string typeId)
        {
            if (!IsOpen)
            {
                Open();
            }
            SelectedTypeId = typeId;
            DemolishMode = false;
            _previewKey = int.MinValue;
            Revision++;
        }

        public void ClearSelection()
        {
            SelectedTypeId = null;
            Preview = null;
            _previewKey = int.MinValue;
            Revision++;
        }

        public void SetDemolishMode(bool on)
        {
            if (on && !IsOpen)
            {
                Open();
            }
            DemolishMode = on;
            if (on)
            {
                SelectedTypeId = null;
                Preview = null;
            }
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>旋转虚影 90°（顺时针）。</summary>
        public void RotateGhost()
        {
            GhostRotation = GridMath.NormalizeRotation(GhostRotation + 90);
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>把鼠标（或自检）指向某一格。</summary>
        public void SetHover(CampaignState state, GridCell cell)
        {
            if (HasHover && HoverCell == cell)
            {
                return;
            }
            HasHover = true;
            HoverCell = cell;
            HoverBuildingId = state != null ? HomeGridService.BuildingAt(state, cell)?.BuildingId : null;
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>旋转鼠标指着的已有建筑。</summary>
        public GridOpResult RotateHovered(CampaignState state)
        {
            if (state == null || HoverBuildingId == null)
            {
                return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding)), null, RotateFailKey);
            }
            GridOpResult r = HomeGridService.TryRotate(state, HoverBuildingId);
            return Report(r, state, RotateFailKey);
        }

        /// <summary>左键点一格：放置 / 拆除标记（与鼠标同一条路径）。</summary>
        public GridOpResult ClickCell(CampaignState state, GridCell cell)
        {
            SetHover(state, cell);
            if (state == null)
            {
                return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.NoRegion)), null);
            }
            if (DemolishMode)
            {
                BuildingRecord target = HomeGridService.BuildingAt(state, cell);
                if (target == null)
                {
                    return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding)), state, DemolishFailKey);
                }
                if (HomeGridService.DemolishNeedsConfirm(state, target.BuildingId))
                {
                    string id = target.BuildingId;
                    string name = HomeGridService.DisplayName(target.BuildingTypeId);
                    var req = new ConfirmRequest
                    {
                        Title = GameText.Format("ui.build.confirm_title", name),
                        Irreversible = true,
                        ConfirmText = GameText.Get("ui.build.confirm_ok"),
                        CancelText = GameText.Get("ui.build.confirm_cancel"),
                        OnConfirm = () => Report(HomeGridService.TryToggleDemolish(CampaignSession.Current, id), CampaignSession.Current, DemolishFailKey),
                    };
                    req.Consequences.Add(GameText.Format("ui.build.confirm_refund", target.InvestedScrap / 2));
                    if (GridContent.TryGetBuilding(target.BuildingTypeId, out BuildingGrid tg) && tg.Critical == 1)
                    {
                        req.Lines.Add(GameText.Get("ui.build.confirm_critical"));
                    }
                    UiConfirmDialog.Show(req);
                    PendingConfirmBuildingId = id;
                    return new GridOpResult(GridOpResult.Kind.Failed, id);
                }
                return Report(HomeGridService.TryToggleDemolish(state, target.BuildingId), state, DemolishFailKey);
            }
            if (SelectedTypeId != null)
            {
                GridOpResult r = HomeGridService.TryPlace(state, SelectedTypeId, cell, GhostRotation);
                if (!r.Success)
                {
                    GuidanceHooks.Raise(GuidanceHooks.FirstBlockedPlacement);
                }
                return Report(r, state);
            }
            // 空闲状态点建筑：只显示它的信息（名字、朝向、端口），不做任何改动。
            BuildingRecord info = HomeGridService.BuildingAt(state, cell);
            SetStatus(info != null ? DescribeBuilding(info) : string.Empty, false);
            return new GridOpResult(GridOpResult.Kind.Failed, info?.BuildingId);
        }

        /// <summary>拆除确认框正在询问的建筑（自检用）。</summary>
        public string PendingConfirmBuildingId { get; private set; }

        /// <summary>右键：取消选择 → 退出拆除模式 → 退出建造模式（FGR-UX-001）。</summary>
        public void RightClick()
        {
            if (SelectedTypeId != null)
            {
                ClearSelection();
            }
            else if (DemolishMode)
            {
                SetDemolishMode(false);
            }
            else
            {
                Close();
            }
        }

        // ── 每帧（由 HomeValleyController.Update 在战略指令之前调用，战略暂停时也调用）────────────────────

        public void Tick(Camera camera, CampaignState state, bool inStrategyView)
        {
            if (!IsOpen)
            {
                if (inStrategyView && state != null)
                {
                    if (InputRouter.ConsumeAction(GameActionId.OpenBuildMenu, InputScope.Strategy))
                    {
                        Open();
                    }
                    else if (InputRouter.ConsumeAction(GameActionId.DemolishMode, InputScope.Strategy))
                    {
                        SetDemolishMode(true);
                    }
                }
                return;
            }
            if (!inStrategyView || state == null)
            {
                Close(); // 进入接入视角 / 离开家园：建造模式随之退出，不留半开的上下文。
                return;
            }

            InputRouter.SetBuildMode(true);
            UiEscapeStack.Sync(this, true, _escClose);

            if (InputRouter.ConsumeAction(GameActionId.OpenBuildMenu, InputScope.Strategy))
            {
                Close();
                return;
            }
            if (InputRouter.ConsumeAction(GameActionId.DemolishMode, InputScope.Strategy))
            {
                SetDemolishMode(!DemolishMode);
            }
            if (InputRouter.ConsumeAction(GameActionId.Rotate, InputScope.Strategy))
            {
                if (SelectedTypeId != null)
                {
                    RotateGhost();
                }
                else
                {
                    RotateHovered(state);
                }
            }

            if (camera != null && InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 pointer)
                && TryPointerCell(camera, pointer, out GridCell cell))
            {
                SetHover(state, cell);
            }

            if (InputRouter.GetMouseButtonDown(1, InputScope.Strategy))
            {
                RightClick();
                if (!IsOpen)
                {
                    return;
                }
            }
            else if (HasHover && InputRouter.GetMouseButtonDown(0, InputScope.Strategy))
            {
                ClickCell(state, HoverCell);
            }

            RefreshPreview(state);
            RefreshVisuals(state);
        }

        private static bool TryPointerCell(Camera camera, Vector3 screen, out GridCell cell)
        {
            Ray ray = camera.ScreenPointToRay(screen);
            cell = default;
            if (Mathf.Abs(ray.direction.y) < 1e-4f)
            {
                return false;
            }
            float t = -ray.origin.y / ray.direction.y;
            if (t <= 0f)
            {
                return false;
            }
            Vector3 hit = ray.origin + ray.direction * t;
            cell = GridCell.FromWorld(new Vector2(hit.x, hit.z));
            return true;
        }

        /// <summary>重算虚影校验——只在（格子、朝向、选择、建筑记录、废料）变化时做，O(占地)。</summary>
        public void RefreshPreview(CampaignState state)
        {
            if (SelectedTypeId == null || !HasHover || state == null)
            {
                if (Preview != null)
                {
                    Preview = null;
                    Revision++;
                }
                return;
            }
            int key = HashCode(HoverCell.X, HoverCell.Y, GhostRotation, SelectedTypeId.GetHashCode());
            if (key == _previewKey && ReferenceEquals(_previewRecordsRef, state.BuildingRecords) && Mathf.Approximately(_previewScrap, state.Scrap))
            {
                return;
            }
            _previewKey = key;
            _previewRecordsRef = state.BuildingRecords;
            _previewScrap = state.Scrap;
            Preview = HomeGridService.ValidatePlacement(state, SelectedTypeId, HoverCell, GhostRotation, into: _previewBuffer);
            Revision++;
        }

        private static int HashCode(int a, int b, int c, int d) => unchecked(((a * 397) ^ b) * 397 ^ c) * 397 ^ d;

        /// <summary>失败时状态行的前缀文本键：放置 = “不能放置：”，旋转 = “不能旋转：”；拆除的原因本身是完整句子（“受损的建筑不能拆除，请先修复”），不加前缀。</summary>
        private const string PlaceFailKey = "ui.build.invalid";
        private const string RotateFailKey = "ui.build.invalid_rotate";
        private const string DemolishFailKey = null;

        private GridOpResult Report(GridOpResult r, CampaignState state, string failKey = PlaceFailKey)
        {
            LastResult = r;
            PendingConfirmBuildingId = null;
            // 放置 / 旋转 / 拆除都会改变“鼠标指着哪座建筑”，鼠标没换格也要重算（否则放下后立即按 R 会报“这里没有建筑”）。
            if (HasHover && state != null)
            {
                HoverBuildingId = HomeGridService.BuildingAt(state, HoverCell)?.BuildingId;
            }
            string name = r.BuildingId != null && state != null
                ? HomeGridService.DisplayName(HomeGridService.FindBuilding(state, r.BuildingId)?.BuildingTypeId ?? SelectedTypeId)
                : string.Empty;
            switch (r.Outcome)
            {
                case GridOpResult.Kind.Placed:
                    SetStatus(GameText.Format("ui.build.placed", name), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
                    break;
                case GridOpResult.Kind.Rotated:
                    BuildingRecord rb = state != null ? HomeGridService.FindBuilding(state, r.BuildingId) : null;
                    SetStatus(GameText.Format("ui.build.rotated", name,
                        GameText.Get(GridMath.DirTextKey(GridMath.FacingOf(GridMath.NormalizeRotation(rb?.Rotation ?? 0f))))), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.UiClick);
                    break;
                case GridOpResult.Kind.DemolishMarked:
                    SetStatus(GameText.Format("ui.build.demolish_marked", name), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
                    break;
                case GridOpResult.Kind.DemolishUnmarked:
                    SetStatus(GameText.Format("ui.build.demolish_unmarked", name), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.UiClick);
                    break;
                case GridOpResult.Kind.PlanCancelled:
                    SetStatus(GameText.Format("ui.build.plan_cancelled", HomeGridService.DisplayName(TypeOfId(r.BuildingId))), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.UiClick);
                    break;
                default:
                    SetStatus(failKey != null ? GameText.Format(failKey, r.Describe()) : r.Describe(), true);
                    // 三通道反馈（FG00 B07）：画面（虚影红 + 叉）、文字（状态行 + 字幕）、声音（Denied 音效）。
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, r.Describe());
                    break;
            }
            _previewKey = int.MinValue;
            return r;
        }

        private static string TypeOfId(string buildingId)
        {
            string key = HomeValleyController.LocalKey(buildingId) ?? string.Empty;
            int hash = key.IndexOf('#');
            return hash > 0 ? key.Substring(0, hash) : key;
        }

        private void SetStatus(string text, bool isError)
        {
            StatusText = text ?? string.Empty;
            StatusIsError = isError;
            Revision++;
        }

        /// <summary>建筑说明行：名字、朝向、端口（当前语言）。</summary>
        public static string DescribeBuilding(BuildingRecord b)
        {
            int rot = GridMath.NormalizeRotation(b.Rotation);
            string head = GameText.Format("ui.build.hover_building", HomeGridService.DisplayName(b.BuildingTypeId),
                GameText.Get(GridMath.DirTextKey(GridMath.FacingOf(rot))));
            var ports = new List<PortPlacement>(4);
            HomeGridService.PortsOf(b, ports);
            return head + "  " + DescribePorts(ports);
        }

        public static string DescribePorts(List<PortPlacement> ports)
        {
            if (ports.Count == 0)
            {
                return GameText.Get("ui.build.no_ports");
            }
            var parts = new List<string>(ports.Count);
            foreach (PortPlacement p in ports)
            {
                parts.Add(GameText.Format("grid.port.entry", GameText.Get(p.IsOutput ? "grid.port.out" : "grid.port.in"), GameText.Get(GridMath.DirTextKey(p.Dir))));
            }
            return GameText.Format("ui.build.ports", string.Join(GameText.Language == GameLanguage.En ? ", " : "、", parts));
        }

        // ── 表现（占位几何，成对创建 / 销毁）────────────────────────────────────────

        private void EnsureVisuals()
        {
            if (_root != null)
            {
                return;
            }
            _root = new GameObject("[BuildMode]");
            Shader unlit = Shader.Find("Sprites/Default");
            _okMaterial = new Material(unlit) { color = new Color(0.25f, 0.85f, 0.35f, 0.55f) };
            _badMaterial = new Material(unlit) { color = new Color(0.95f, 0.2f, 0.15f, 0.6f) };
            _markMaterial = new Material(unlit) { color = new Color(0.95f, 0.45f, 0.1f, 0.55f) };
            _outMaterial = new Material(unlit) { color = new Color(1f, 0.6f, 0.1f, 0.95f) };
            _inMaterial = new Material(unlit) { color = new Color(0.2f, 0.85f, 0.95f, 0.95f) };
            _overlayMaterial = new Material(unlit) { color = new Color(1f, 1f, 1f, 0.55f) };
            _cross = new GameObject("GhostCross");
            _cross.transform.SetParent(_root.transform, false);
            for (int i = 0; i < 2; i++)
            {
                GameObject bar = NewPrimitive(PrimitiveType.Cube, "Bar" + i, _cross.transform, _badMaterial);
                bar.transform.localRotation = Quaternion.Euler(0f, i == 0 ? 45f : -45f, 0f);
            }
            _cross.SetActive(false);
        }

        private GameObject NewPrimitive(PrimitiveType type, string name, Transform parent, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Collider c = go.GetComponent<Collider>();
            if (c != null)
            {
                Object.Destroy(c); // 不挡选中射线。
            }
            go.transform.SetParent(parent, false);
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        private void DestroyVisuals()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
            _tiles.Clear();
            _arrows.Clear();
            _cross = null;
            _overlay = null;
            DestroyAsset(ref _overlayTexture);
            DestroyAsset(ref _overlayMaterial);
            DestroyAsset(ref _okMaterial);
            DestroyAsset(ref _badMaterial);
            DestroyAsset(ref _markMaterial);
            DestroyAsset(ref _outMaterial);
            DestroyAsset(ref _inMaterial);
        }

        private static void DestroyAsset<T>(ref T asset) where T : Object
        {
            if (asset != null)
            {
                Object.Destroy(asset);
                asset = null;
            }
        }

        /// <summary>离开家园时调用：关闭建造模式并释放全部表现资源。</summary>
        public void Shutdown()
        {
            Close();
            DestroyVisuals();
            InputRouter.SetBuildMode(false);
        }

        /// <summary>按当前预览 / 悬停刷新虚影格、叉形与端口箭头（Tick 每帧调用；自检直接调用后读 <see cref="GhostCrossVisible"/> 等）。</summary>
        public void RefreshVisuals(CampaignState state)
        {
            if (_root == null)
            {
                return;
            }
            int tile = 0;
            int arrow = 0;
            bool showCross = false;
            if (Preview != null)
            {
                for (int i = 0; i < Preview.Cells.Count; i++)
                {
                    PlaceTile(tile++, Preview.Cells[i], Preview.CellOk[i] && Preview.Ok ? _okMaterial : _badMaterial, 0.35f);
                }
                if (!Preview.Ok && Preview.Cells.Count > 0)
                {
                    showCross = true;
                    Vector2 c = GridMath.FootprintCenter(Preview.Pivot, 1, 1, 0);
                    if (GridContent.TryGetBuilding(Preview.TypeId, out BuildingGrid g))
                    {
                        c = GridMath.FootprintCenter(Preview.Pivot, g.FootprintW, g.FootprintH, Preview.Rotation);
                        Vector2Int size = GridMath.RotatedSize(g.FootprintW, g.FootprintH, Preview.Rotation);
                        float len = Mathf.Sqrt(size.x * size.x + size.y * size.y);
                        foreach (Transform bar in _cross.transform)
                        {
                            bar.localScale = new Vector3(len, 0.15f, 0.35f);
                        }
                    }
                    _cross.transform.position = new Vector3(c.x, 0.6f, c.y);
                }
                HomeGridService.PortsFor(Preview.TypeId, Preview.Pivot, Preview.Rotation, _ports);
                foreach (PortPlacement p in _ports)
                {
                    PlaceArrow(arrow++, p);
                }
            }
            else if (HoverBuildingId != null)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, HoverBuildingId);
                if (b != null && GridContent.TryGetBuilding(b.BuildingTypeId, out _))
                {
                    HomeGridService.FootprintOf(b, _cells);
                    Material m = DemolishMode ? _badMaterial : _okMaterial;
                    foreach (GridCell c in _cells)
                    {
                        PlaceTile(tile++, c, m, 2.2f);
                    }
                    HomeGridService.PortsOf(b, _ports);
                    foreach (PortPlacement p in _ports)
                    {
                        PlaceArrow(arrow++, p);
                    }
                }
            }
            for (int i = tile; i < _tiles.Count; i++)
            {
                _tiles[i].SetActive(false);
            }
            for (int i = arrow; i < _arrows.Count; i++)
            {
                _arrows[i].SetActive(false);
            }
            _cross.SetActive(showCross);
        }

        private void PlaceTile(int index, GridCell cell, Material material, float height)
        {
            while (_tiles.Count <= index)
            {
                GameObject t = NewPrimitive(PrimitiveType.Cube, "GhostTile" + _tiles.Count, _root.transform, _okMaterial);
                t.transform.localScale = new Vector3(0.9f, 0.08f, 0.9f);
                _tiles.Add(t);
            }
            GameObject go = _tiles[index];
            go.SetActive(true);
            go.transform.position = new Vector3(cell.X, height, cell.Y);
            Renderer r = go.GetComponent<Renderer>();
            if (r.sharedMaterial != material)
            {
                r.sharedMaterial = material;
            }
        }

        private void PlaceArrow(int index, PortPlacement port)
        {
            while (_arrows.Count <= index)
            {
                var a = new GameObject("PortArrow" + _arrows.Count);
                a.transform.SetParent(_root.transform, false);
                NewPrimitive(PrimitiveType.Cube, "Shaft", a.transform, _outMaterial);
                NewPrimitive(PrimitiveType.Cube, "Head", a.transform, _outMaterial);
                _arrows.Add(a);
            }
            GameObject go = _arrows[index];
            go.SetActive(true);
            Vector2Int d = GridMath.DirVector(port.Dir);
            // 输出：长箭头朝外；输入：短箭头朝里（形状区分，不只靠颜色）。
            float length = port.IsOutput ? 1.1f : 0.7f;
            Vector3 origin = new Vector3(port.Cell.X + d.x * 0.5f, 2.4f, port.Cell.Y + d.y * 0.5f);
            Vector3 dir = new Vector3(d.x, 0f, d.y) * (port.IsOutput ? 1f : -1f);
            Vector3 start = port.IsOutput ? origin : origin - dir * length;
            go.transform.position = start;
            go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            Transform shaft = go.transform.Find("Shaft");
            Transform head = go.transform.Find("Head");
            shaft.localScale = new Vector3(0.18f, 0.18f, length);
            shaft.localPosition = new Vector3(0f, 0f, length * 0.5f);
            head.localScale = new Vector3(0.45f, 0.2f, 0.3f);
            head.localPosition = new Vector3(0f, 0f, length);
            Material m = port.IsOutput ? _outMaterial : _inMaterial;
            shaft.GetComponent<Renderer>().sharedMaterial = m;
            head.GetComponent<Renderer>().sharedMaterial = m;
        }

        // ── 地形叠加层（打开建造模式时生成一次；数据全部来自格网）──────────────────────────────

        private const int PixelsPerCell = 6;

        /// <summary>重画核心周围的地形叠加层（颜色 + 图案 + 迷雾 + 污染 + 核心通道）。只在打开建造模式时调用。</summary>
        public void RebuildOverlay()
        {
            CampaignState state = CampaignSession.Current;
            if (_root == null || state == null)
            {
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            GridCell core = HomeGridService.CorePivot(state);
            int radius = Mathf.Max(4, GridContent.TuningInt("grid.overlay_radius"));
            int cells = radius * 2 + 1;
            int size = cells * PixelsPerCell;
            DestroyAsset(ref _overlayTexture);
            _overlayTexture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            int ring = GridContent.TuningInt("grid.core_reserve_ring");
            int blockLevel = GridContent.TuningInt("grid.pollution_block_level");
            BuildingRecord coreRecord = null;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore && b.RegionId == HomeValleyLayout.RegionId)
                {
                    coreRecord = b;
                    break;
                }
            }
            GridCell coreMin = core, coreMax = core;
            if (coreRecord != null && GridContent.TryGetBuilding(coreRecord.BuildingTypeId, out BuildingGrid cg))
            {
                GridMath.FootprintBounds(new GridCell(coreRecord.GridX, coreRecord.GridY), cg.FootprintW, cg.FootprintH,
                    GridMath.NormalizeRotation(coreRecord.Rotation), out coreMin, out coreMax);
            }
            for (int cy = 0; cy < cells; cy++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    var cell = new GridCell(core.X - radius + cx, core.Y - radius + cy);
                    GridTerrain t = GridContent.TerrainByCode(map.GetTerrain(cell));
                    Color32 baseColor = t != null && ColorUtility.TryParseHtmlString(t.Color, out Color c) ? (Color32)c : new Color32(90, 90, 90, 255);
                    string pattern = t?.Pattern ?? "none";
                    bool explored = map.IsExplored(cell);
                    int pollution = map.GetPollution(cell);
                    bool reserve = cell.X >= coreMin.X - ring && cell.X <= coreMax.X + ring && cell.Y >= coreMin.Y - ring && cell.Y <= coreMax.Y + ring
                                   && !(cell.X >= coreMin.X && cell.X <= coreMax.X && cell.Y >= coreMin.Y && cell.Y <= coreMax.Y);
                    for (int py = 0; py < PixelsPerCell; py++)
                    {
                        for (int px = 0; px < PixelsPerCell; px++)
                        {
                            Color32 col = baseColor;
                            if (PatternPixel(pattern, px, py))
                            {
                                col = Shade(col, 0.55f);
                            }
                            if (pollution > 0 && (px + PixelsPerCell - 1 - py) % (pollution >= blockLevel ? 2 : 3) == 0)
                            {
                                col = new Color32(150, 40, 150, 255); // 污染：紫色反斜线，≥ 不可建等级时更密。
                            }
                            if (reserve && (px == 0 || py == 0 || px == PixelsPerCell - 1 || py == PixelsPerCell - 1))
                            {
                                col = new Color32(230, 200, 40, 255); // 核心通道：黄色框。
                            }
                            else if (px == 0 || py == 0)
                            {
                                col = Shade(col, 0.8f); // 格线（FG03 第 4 节“格线显示”）。
                            }
                            if (!explored)
                            {
                                col = Shade(col, 0.25f); // 迷雾：大幅变暗。
                            }
                            pixels[(cy * PixelsPerCell + py) * size + cx * PixelsPerCell + px] = col;
                        }
                    }
                }
            }
            _overlayTexture.SetPixels32(pixels);
            _overlayTexture.Apply(false, false);
            if (_overlay == null)
            {
                _overlay = NewPrimitive(PrimitiveType.Quad, "TerrainOverlay", _root.transform, _overlayMaterial);
                _overlay.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
            _overlayMaterial.mainTexture = _overlayTexture;
            _overlay.transform.position = new Vector3(core.X, 0.03f, core.Y);
            _overlay.transform.localScale = new Vector3(cells, cells, 1f);
            Revision++;
        }

        private static bool PatternPixel(string pattern, int x, int y)
        {
            int n = PixelsPerCell;
            switch (pattern)
            {
                case "hatch": return (x + y) % 3 == 0;
                case "dots": return (x == n / 2 || x == n / 2 - 1) && (y == n / 2 || y == n / 2 - 1);
                case "waves": return y == n / 2 + ((x / 2) % 2 == 0 ? 0 : 1);
                case "cross": return x == y || x == n - 1 - y;
                case "grid": return x % 3 == 1 || y % 3 == 1;
                default: return false;
            }
        }

        private static Color32 Shade(Color32 c, float k) => new Color32((byte)(c.r * k), (byte)(c.g * k), (byte)(c.b * k), c.a);

        /// <summary>叠加层纹理（自检读像素用）。</summary>
        public Texture2D OverlayTexture => _overlayTexture;
        public int OverlayRadius => GridContent.TuningInt("grid.overlay_radius");
        public bool GhostCrossVisible => _cross != null && _cross.activeSelf;

        /// <summary>第 <paramref name="index"/> 个端口箭头的朝向（世界 xz 平面；自检核对箭头方向与端口一致）。</summary>
        public Vector3 ArrowForward(int index) => index < _arrows.Count ? _arrows[index].transform.forward : Vector3.zero;
        public int ActiveArrowCount
        {
            get
            {
                int n = 0;
                foreach (GameObject a in _arrows)
                {
                    if (a.activeSelf)
                    {
                        n++;
                    }
                }
                return n;
            }
        }
    }
}
