using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG0-ARCH-04 原型 → FG3-LOG-01 正式版（FGR-LOG-002～004、007、008、012、013；FG13 FGU-07 / FGU-08；FGT-LOG-001）：家园的建造模式。
    ///
    /// 输入（全部经 <see cref="InputRouter"/> 的动作与上下文，按键可重绑，FG00 B02）：
    /// - 建造菜单键（默认 B）打开 / 关闭；拆除模式键（默认 X）；搬迁键（默认 E）；旋转键（默认 R）；格线开关（默认 G）；快捷栏 1～10（默认 F1～F10）；
    /// - 选中建筑后虚影跟随鼠标、吸附格网，左键放置（合法才放，非法给原因）；
    /// - 选中工具（传送带）后按住左键拖拽铺设：路径自动转角，拖的时候显示长度与成本，松开时全有或全无地铺下；单击 = 一格（方向按旋转键）；
    /// - 拆除模式：左键点建筑标记 / 取消标记（关键建筑先确认）；在空地按住左键框选批量拆除（超过 20 座或含关键建筑先确认）；
    /// - 搬迁：搬迁模式里左键点一座建筑、再左键点新位置（旋转键转向）；空闲状态下也可以直接按住一座建筑拖到新位置；
    /// - 右键：取消拖拽 → 放下正在搬的建筑 → 取消选择 → 退出搬迁 / 拆除模式 → 退出建造模式；Esc 退出建造模式（经 <see cref="UiEscapeStack"/>）。
    /// - FG3-LOG-07 规划工具：复制（默认 Ctrl+C，拖框复制框里的东西连同设置，复制完直接进入粘贴）、粘贴（Ctrl+V，旋转键整体转 90°，
    ///   非法的件标红叉不放）、布局库（Ctrl+B）、升级规划（U，拖框）、吸管（Q，指着一件东西选中同类并带上朝向与设置）、
    ///   复制设置（Alt+C 记下指着的那件的设置 / Alt+V 写到指着的那件；也可以点按钮进入“复制设置”模式用鼠标点）、撤销 / 重做（Ctrl+Z / Ctrl+Y）。
    ///   规划操作全部经 <see cref="PlanHistory"/> 的包装入口，进撤销栈。
    /// 战略暂停下可以继续规划，施工在恢复后推进（FG03 第 4 节）。
    ///
    /// 表现（占位，FG00 B22）：虚影逐格着色（绿 = 可放、红 = 不可放），不合法时再叠一个“叉”形（色盲安全，颜色之外有形状）；
    /// 端口用箭头（输出长箭头、输入短箭头）；拖拽路径逐格着色，第一处不合法的格子叠叉；框选画出框的四条边；
    /// 地形叠加层（<see cref="WorldTerrainOverlay"/>）画格线（可关）、地形颜色 + 图案、迷雾变暗、污染斜线、核心通道黄框。
    ///
    /// 状态只读写 <see cref="HomeGridService"/> / <see cref="BuildCatalog"/>；本类不保存任何需要进存档的东西（选择、朝向、拖拽是界面状态；
    /// 快捷栏在存档里、格线开关在本机设置里）。每帧开销与建筑数无关：鼠标换格 / 换朝向 / 状态变化时才重算一次校验。
    /// </summary>
    public sealed partial class HomeValleyBuildMode
    {
        /// <summary>当前家园的建造模式（家园未激活时为 null）。HUD 与自检读这里。</summary>
        public static HomeValleyBuildMode Current { get; private set; }

        /// <summary>拖拽的种类。</summary>
        public enum DragKind : byte
        {
            None,
            /// <summary>选中传送带后按住左键拖路径。</summary>
            Belt,
            /// <summary>拆除模式下在空地按住左键拉框。</summary>
            DemolishBox,
            /// <summary>空闲状态下按住一座建筑拖到新位置（搬迁）。</summary>
            Relocate,
            /// <summary>FG3-LOG-02：“优先建造这一片”模式下拉框。</summary>
            PrioritizeBox,
            /// <summary>FG3-LOG-03：清带模式下拉框（点一格 = 整条带）。</summary>
            ClearBox,
            /// <summary>FG3-LOG-07：复制模式下拉框（松开 = 复制框里的东西）。</summary>
            CopyBox,
            /// <summary>FG3-LOG-07：升级规划模式下拉框。</summary>
            UpgradeBox,
        }

        private static readonly GameActionId[] HotbarActions =
        {
            GameActionId.Hotbar1, GameActionId.Hotbar2, GameActionId.Hotbar3, GameActionId.Hotbar4, GameActionId.Hotbar5,
            GameActionId.Hotbar6, GameActionId.Hotbar7, GameActionId.Hotbar8, GameActionId.Hotbar9, GameActionId.Hotbar10,
        };

        public bool IsOpen { get; private set; }
        public bool DemolishMode { get; private set; }
        /// <summary>FG3-LOG-01：搬迁模式（点一座建筑、再点新位置）。</summary>
        public bool RelocateMode { get; private set; }
        /// <summary>搬迁模式里已经点起来、正跟着鼠标的建筑（还没放下时为 null）。</summary>
        public string CarryBuildingId { get; private set; }
        /// <summary>FG3-LOG-02（FG03 第 4 节“优先建造这一片”）：拖框把框里的施工改成最高优先级。</summary>
        public bool PrioritizeMode { get; private set; }
        /// <summary>“优先建造这一片”拉框时框里的施工现场数（没在拉框时为 -1）。</summary>
        public int PrioritizeBoxCount { get; private set; } = -1;
        public GridCell PrioritizeBoxMin { get; private set; }
        public GridCell PrioritizeBoxMax { get; private set; }
        /// <summary>FG3-LOG-03（FGR-LOG-026）：清带模式——点一格传送带（整条带）或拖框，把上面的物品送到最近的仓库。</summary>
        public bool ClearMode { get; private set; }
        /// <summary>清带拉框时的规划（格数、物品数）；没在拉框时为 null。</summary>
        public BeltClearPlan ClearPlan { get; private set; }
        /// <summary>清带确认框（仓库放不下，问是否丢弃）是否正在询问（自检用）。</summary>
        public bool PendingClearConfirm { get; private set; }
        /// <summary>FG3-LOG-07：复制模式（拖框复制）。</summary>
        public bool CopyMode { get; private set; }
        /// <summary>FG3-LOG-07：升级规划模式（拖框升级）。</summary>
        public bool UpgradeMode { get; private set; }
        /// <summary>FG3-LOG-07：粘贴模式——<see cref="PasteSource"/> 跟着鼠标，左键放下，旋转键整体转 90°，右键退出。</summary>
        public bool PasteMode { get; private set; }
        /// <summary>FG3-LOG-07：“复制设置”模式（按钮进入：先点一件记下设置，再点别的件写上去）。</summary>
        public bool SettingsMode { get; private set; }
        /// <summary>FG3-LOG-07：剪贴板（最近一次复制的布局；跨地点保留，不进存档）。</summary>
        public static PlanEntryBlock Clipboard { get; private set; }
        /// <summary>FG3-LOG-07：复制设置的剪贴板（不进存档）。</summary>
        public static PlanSettings.Clip SettingsClip { get; private set; }
        /// <summary>正在粘贴的布局（剪贴板或布局库里的一个）。</summary>
        public PlanEntryBlock PasteSource { get; private set; }
        /// <summary>正在粘贴的布局名（布局库里的；剪贴板为空串）。</summary>
        public string PasteName { get; private set; } = string.Empty;
        /// <summary>粘贴的整体旋转（0～3 个 90°，顺时针）。</summary>
        public int PasteQuarter { get; private set; }
        /// <summary>粘贴预览（逐件能不能放）；不在粘贴或没有悬停格时为 null。</summary>
        public PastePlan PastePreview { get; private set; }
        /// <summary>拖复制框时的框（没在拖时无意义）。</summary>
        public GridCell CopyBoxMin { get; private set; }
        public GridCell CopyBoxMax { get; private set; }
        /// <summary>拖升级框时的规划；没在拖时为 null。</summary>
        public UpgradeBoxPlan UpgradePreview { get; private set; }
        /// <summary>FG3-LOG-07 吸管：下一次放置要带上的设置（S0～S2；0 = 默认）。换选择时清掉。</summary>
        public int PendingS0 { get; private set; }
        public int PendingS1 { get; private set; }
        public int PendingS2 { get; private set; }
        /// <summary>最近一次撤销 / 重做的结果（自检读）。</summary>
        public PlanStepResult LastStep { get; private set; }
        /// <summary>最近一次粘贴的结果（自检读）。</summary>
        public PlanApplyResult LastPaste { get; private set; }
        /// <summary>粘贴预览因为太多被截掉没画的格子数（状态行写明）。</summary>
        public int PasteTilesHidden { get; private set; }
        public string SelectedTypeId { get; private set; }
        /// <summary>FG3-LOG-01：选中的建造菜单工具（传送带）。与 <see cref="SelectedTypeId"/> 互斥。</summary>
        public string SelectedToolId { get; private set; }
        public int GhostRotation { get; private set; }
        public bool HasHover { get; private set; }
        public GridCell HoverCell { get; private set; }
        public string HoverBuildingId { get; private set; }
        /// <summary>当前虚影的校验结果（没有选中建筑 / 没有在搬迁时为 null）。</summary>
        public GridPlacementResult Preview { get; private set; }
        /// <summary>当前拖拽（种类、起点）；<see cref="Drag"/> = None 时没有在拖。</summary>
        public DragKind Drag { get; private set; }
        public GridCell DragStart { get; private set; }
        /// <summary>拖拽传送带时的路径规划（长度、成本、逐格合法性）；没在拖传送带时为 null。</summary>
        public BeltPathPlan BeltPlan { get; private set; }
        /// <summary>FG3-LOG-04：选中工具但没在拖时，鼠标指着那一格的放置预览（能不能放、朝向、分流器 / 合流器的进出口箭头）；没选工具时为 null。</summary>
        public BeltPathPlan ToolPreview { get; private set; }
        /// <summary>框选拆除时的规划；没在拉框时为 null。</summary>
        public DemolishBoxPlan BoxPlan { get; private set; }
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
        private readonly BeltPathPlan _beltBuffer = new BeltPathPlan();
        private readonly BeltPathPlan _toolPreviewBuffer = new BeltPathPlan();
        private int _toolPreviewKey = int.MinValue;
        private readonly DemolishBoxPlan _boxBuffer = new DemolishBoxPlan();
        private readonly BeltClearPlan _clearBuffer = new BeltClearPlan();
        private readonly PastePlan _pasteBuffer = new PastePlan();
        private readonly UpgradeBoxPlan _upgradeBuffer = new UpgradeBoxPlan();
        private int _pasteKey = int.MinValue;
        private BuildingRecord[] _pasteRecordsRef;
        private readonly List<GameObject> _marks = new List<GameObject>(16);
        private GameObject _root;
        private WorldTerrainOverlay _terrain;

        /// <summary>FG3-LOG-08：建造叠加层的地形贴图（自检读它的污染视图开关）。</summary>
        public WorldTerrainOverlay TerrainOverlayForTests => _terrain;
        private int _terrainRevision = -1;
        private Material _okMaterial;
        private Material _badMaterial;
        private Material _markMaterial;
        private Material _outMaterial;
        private Material _inMaterial;
        private readonly List<GameObject> _tiles = new List<GameObject>(32);
        private readonly List<GameObject> _arrows = new List<GameObject>(8);
        private readonly List<GameObject> _boxEdges = new List<GameObject>(4);
        private GameObject _cross;
        private int _previewKey = int.MinValue;
        private BuildingRecord[] _previewRecordsRef;
        private float _previewScrap = -1f;
        private string _dragBuildingId;
        private GridCell _dragBuildingPivot;
        private int _dragBuildingRotation;
        private GridCell _dragLast;

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

        /// <summary>FG3-LOG-07：退出复制 / 粘贴 / 升级 / 复制设置模式（进入别的模式或选中条目时调用）。</summary>
        private void ExitPlanModes()
        {
            CopyMode = false;
            UpgradeMode = false;
            PasteMode = false;
            SettingsMode = false;
            PasteSource = null;
            PastePreview = null;
            _pasteKey = int.MinValue;
        }

        public void Open()
        {
            if (IsOpen)
            {
                return;
            }
            IsOpen = true;
            ExitPlanModes();
            DemolishMode = false;
            RelocateMode = false;
            PrioritizeMode = false;
            ClearMode = false;
            CarryBuildingId = null;
            SelectedTypeId = null;
            SelectedToolId = null;
            CancelDrag();
            InputRouter.SetBuildMode(true);
            GuidanceHooks.Raise(GuidanceHooks.BuildModeFirstOpen);
            EnsureVisuals();
            CampaignState openState = CampaignSession.Current;
            if (openState != null)
            {
                UpdateTerrainOverlay(openState, HomeGridService.CorePivot(openState));
            }
            SetStatus(string.Empty, false);
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }
            IsOpen = false;
            ExitPlanModes();
            DemolishMode = false;
            RelocateMode = false;
            PrioritizeMode = false;
            ClearMode = false;
            CarryBuildingId = null;
            SelectedTypeId = null;
            SelectedToolId = null;
            CancelDrag();
            Preview = null;
            ToolPreview = null;
            InputRouter.SetBuildMode(false);
            UiEscapeStack.Remove(this);
            DestroyVisuals();
            Revision++;
        }

        /// <summary>选中要放置的建筑或工具（进入放置状态，退出拆除 / 搬迁模式）。不可放置 / 未解锁的也能选中，虚影会给出原因。
        /// <paramref name="entryId"/> 是建造菜单条目 ID：建筑类型 ID，或工具 ID（传送带）。</summary>
        public void Select(string entryId)
        {
            if (!IsOpen)
            {
                Open();
            }
            bool tool = BuildCatalog.TryGet(entryId, out BuildEntry e) && e.IsTool;
            ExitPlanModes();
            PendingS0 = PendingS1 = PendingS2 = 0; // 吸管带的设置只跟着吸出来的那一次选择
            SelectedTypeId = tool ? null : entryId;
            SelectedToolId = tool ? entryId : null;
            DemolishMode = false;
            RelocateMode = false;
            PrioritizeMode = false;
            ClearMode = false;
            CarryBuildingId = null;
            CancelDrag();
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>选中的条目 ID（建筑或工具）。</summary>
        public string SelectedEntryId => SelectedToolId ?? SelectedTypeId;

        public void ClearSelection()
        {
            SelectedTypeId = null;
            SelectedToolId = null;
            Preview = null;
            ToolPreview = null;
            CancelDrag();
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
                ExitPlanModes();
                SelectedTypeId = null;
                SelectedToolId = null;
                RelocateMode = false;
                PrioritizeMode = false;
                ClearMode = false;
                CarryBuildingId = null;
                Preview = null;
            }
            CancelDrag();
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>FG3-LOG-01：搬迁模式开关（按钮与搬迁键同一路径）。</summary>
        public void SetRelocateMode(bool on)
        {
            if (on && !IsOpen)
            {
                Open();
            }
            RelocateMode = on;
            CarryBuildingId = null;
            if (on)
            {
                ExitPlanModes();
                SelectedTypeId = null;
                SelectedToolId = null;
                DemolishMode = false;
                PrioritizeMode = false;
                ClearMode = false;
            }
            Preview = null;
            CancelDrag();
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>FG3-LOG-02：“优先建造这一片”模式开关（按钮与快捷键同一路径）。进入时退出放置 / 拆除 / 搬迁。</summary>
        public void SetPrioritizeMode(bool on)
        {
            if (on && !IsOpen)
            {
                Open();
            }
            PrioritizeMode = on;
            if (on)
            {
                ExitPlanModes();
                SelectedTypeId = null;
                SelectedToolId = null;
                DemolishMode = false;
                RelocateMode = false;
                ClearMode = false;
                CarryBuildingId = null;
                Preview = null;
                SetStatus(GameText.Get("ui.build.prioritize_mode"), false);
            }
            CancelDrag();
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>FG3-LOG-03（FGR-LOG-026）：清带模式开关（按钮与快捷键同一路径）。进入时退出放置 / 拆除 / 搬迁 / 优先建造。</summary>
        public void SetClearMode(bool on)
        {
            if (on && !IsOpen)
            {
                Open();
            }
            ClearMode = on;
            if (on)
            {
                ExitPlanModes();
                SelectedTypeId = null;
                SelectedToolId = null;
                DemolishMode = false;
                RelocateMode = false;
                PrioritizeMode = false;
                CarryBuildingId = null;
                Preview = null;
                SetStatus(GameText.Get("ui.build.clear_mode"), false);
            }
            CancelDrag();
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>旋转虚影 90°（顺时针）。选中传送带时是单格铺设的方向；搬迁时是新位置的朝向。</summary>
        public void RotateGhost()
        {
            if (PasteMode)
            {
                PasteQuarter = (PasteQuarter + 1) & 3; // FG3-LOG-07：粘贴时旋转键把整个布局顺时针转 90°
                _pasteKey = int.MinValue;
                Revision++;
                return;
            }
            GhostRotation = GridMath.NormalizeRotation(GhostRotation + 90);
            _previewKey = int.MinValue;
            Revision++;
        }

        /// <summary>FG3-LOG-01：格线开关（本机设置，跨战役保留）。</summary>
        public void ToggleGridLines()
        {
            GameSettings.SetBuildGridLinesEnabled(!GameSettings.BuildGridLinesEnabled);
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
            if (Drag != DragKind.None && state != null)
            {
                RefreshDrag(state);
            }
        }

        /// <summary>旋转鼠标指着的已有建筑。</summary>
        public GridOpResult RotateHovered(CampaignState state)
        {
            // FG3-LOG-05（FGR-LOG-043）：指着已建成的阀门按旋转键 = 调头（上下游对调）；两侧流体不同时拒绝并写明原因。
            if (state != null && HoverBuildingId == null && HasHover && PipeNetworkService.TryGetPiece(HoverCell, out PipePieceKind pk, out _) && pk == PipePieceKind.Valve)
            {
                return ReverseHoveredValve(state);
            }
            // FG3-LOG-03（FGR-LOG-020“可以原地反转方向”）：指着一格已建成的传送带按旋转键 = 原地反转（物品位置镜像，不丢不增）。
            if (state != null && HoverBuildingId == null && HasHover && BeltNetworkService.IsRunning && BeltNetworkService.Kernel.HasCell(HoverCell.X, HoverCell.Y))
            {
                return ReverseHoveredBelt(state);
            }
            if (state == null || HoverBuildingId == null)
            {
                return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding)), null, RotateFailKey);
            }
            GridOpResult r = PlanHistory.Rotate(state, HoverBuildingId);
            return Report(r, state, RotateFailKey);
        }

        /// <summary>FG3-LOG-03：原地反转鼠标指着的传送带（成功写“现在朝 X”，失败写原因）。</summary>
        public GridOpResult ReverseHoveredBelt(CampaignState state)
        {
            // FG3-LOG-04：分流器 / 合流器 = 原地顺时针转 90°（物品不丢）；地下传送带不能原地转（写原因：拆掉重拖）。
            if (BeltNetworkService.TryGetPiece(HoverCell, out BeltNodeKind kind, out int tier) && kind != BeltNodeKind.Belt)
            {
                BeltOpResult nr = PlanHistory.RotateNode(state, HoverCell);
                if (!nr.Ok)
                {
                    LastResult = GridOpResult.Fail(new GridReason(GridBlockReason.Busy, nr.ReasonKey, nr.Args));
                    SetStatus(nr.Describe(), true);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                    return LastResult;
                }
                BeltNetworkService.Kernel.TryGetCellInfo(HoverCell.X, HoverCell.Y, out BeltCellInfo turned);
                LastResult = new GridOpResult(GridOpResult.Kind.BeltReversed, null);
                SetStatus(GameText.Format("ui.build.node_rotated", BeltNetworkService.PieceName(kind, tier), HoverCell.X, HoverCell.Y,
                    GameText.Get(GridMath.DirTextKey((GridDir)turned.Dir))), false);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
                return LastResult;
            }
            BeltOpResult r = PlanHistory.ReverseBelt(state, HoverCell);
            if (!r.Ok)
            {
                LastResult = GridOpResult.Fail(new GridReason(GridBlockReason.Busy, r.ReasonKey, r.Args));
                SetStatus(r.Describe(), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return LastResult;
            }
            BeltNetworkService.Kernel.TryGetCellInfo(HoverCell.X, HoverCell.Y, out BeltCellInfo info);
            LastResult = new GridOpResult(GridOpResult.Kind.BeltReversed, null);
            SetStatus(GameText.Format("ui.build.belt_reversed", HoverCell.X, HoverCell.Y, GameText.Get(GridMath.DirTextKey((GridDir)info.Dir))), false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
            return LastResult;
        }

        /// <summary>FG3-LOG-05：阀门原地调头（成功写“现在流向 X”，失败写原因）。</summary>
        public GridOpResult ReverseHoveredValve(CampaignState state)
        {
            PipeOpResult r = PlanHistory.ReverseValve(state, HoverCell);
            if (!r.Ok)
            {
                LastResult = GridOpResult.Fail(new GridReason(GridBlockReason.Busy, r.ReasonKey, r.Args));
                SetStatus(r.Describe(), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return LastResult;
            }
            PipeNetworkService.Kernel.TryGetCellInfo(HoverCell.X, HoverCell.Y, out PipeCellInfo info);
            LastResult = new GridOpResult(GridOpResult.Kind.BeltReversed, null);
            SetStatus(GameText.Format("ui.build.valve_reversed", HoverCell.X, HoverCell.Y, GameText.Get(GridMath.DirTextKey((GridDir)info.Dir))), false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
            return LastResult;
        }

        // ── 快捷栏（FGR-LOG-002）─────────────────────────────────────────────────────

        /// <summary>按下快捷栏第 <paramref name="slot"/>（0～9）格：选中里面的条目；空格子给提示。建造模式没开时先打开。</summary>
        public void PressHotbar(CampaignState state, int slot)
        {
            BuildEntry e = BuildCatalog.HotbarEntry(state, slot);
            if (!IsOpen)
            {
                Open();
            }
            if (e == null)
            {
                SetStatus(GameText.Format("ui.hotbar.empty_pressed", slot + 1), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return;
            }
            Select(e.Id);
        }

        /// <summary>把条目放进快捷栏第 <paramref name="slot"/> 格（拖放与“选中后点空格子”同一入口）；<paramref name="entryId"/> 为空 = 清空。</summary>
        public bool AssignHotbar(CampaignState state, int slot, string entryId)
        {
            if (!BuildCatalog.TrySetHotbar(state, slot, entryId))
            {
                return false;
            }
            SetStatus(string.IsNullOrEmpty(entryId)
                ? GameText.Format("ui.hotbar.cleared", slot + 1)
                : GameText.Format("ui.hotbar.assigned", slot + 1, BuildCatalog.TryGet(entryId, out BuildEntry e) ? e.Name : entryId), false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.UiClick);
            return true;
        }

        // ── 点击与拖拽 ──────────────────────────────────────────────────────────────

        /// <summary>左键点一格：放置 / 拆除标记 / 搬迁（点起或放下） / 查看（与鼠标按下同一条路径）。传送带与框选走 <see cref="PointerDown"/>。</summary>
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
                    // FG3-LOG-05：储罐里还有流体时先确认（拆除会排空、不返还；FG00 B04 不可逆）。
                    if (PipeNetworkService.IsRunning && PipeNetworkService.Kernel.TryGetCellInfo(cell.X, cell.Y, out PipeCellInfo tank) && tank.TankStockMl > 0)
                    {
                        AskRemoveTank(cell, tank);
                        return new GridOpResult(GridOpResult.Kind.Failed, null);
                    }
                    if (HomeGridService.MapFor(state).GetBelt(cell) != 0 || HomeGridService.MapFor(state).GetPipe(cell) != 0)
                    {
                        _cells.Clear();
                        _cells.Add(cell);
                        return Report(PlanHistory.RemoveCells(state, _cells), state, DemolishFailKey);
                    }
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
                        OnConfirm = () => Report(PlanHistory.ToggleDemolish(CampaignSession.Current, id), CampaignSession.Current, DemolishFailKey),
                    };
                    req.Consequences.Add(GameText.Format("ui.build.confirm_refund", target.InvestedScrap)); // FG3-LOG-02：全额返还。
                    if (GridContent.TryGetBuilding(target.BuildingTypeId, out BuildingGrid tg) && tg.Critical == 1)
                    {
                        req.Lines.Add(GameText.Get("ui.build.confirm_critical"));
                    }
                    HomeValleyPowerGrid.AppendRemovalLines(state, id, req.Consequences); // FG3-LOG-06：拆电塔会让电网断开 / 建筑失去电网连接时写明。
                    UiConfirmDialog.Show(req);
                    PendingConfirmBuildingId = id;
                    return new GridOpResult(GridOpResult.Kind.Failed, id);
                }
                return Report(PlanHistory.ToggleDemolish(state, target.BuildingId), state, DemolishFailKey);
            }
            if (PasteMode)
            {
                return CommitPaste(state);
            }
            if (SettingsMode)
            {
                return SettingsClip == null ? CopySettingsAt(state, cell) : PasteSettingsAt(state, cell);
            }
            if (RelocateMode)
            {
                if (CarryBuildingId == null)
                {
                    return PickForRelocation(state, cell);
                }
                GridOpResult moved = PlanHistory.Relocate(state, CarryBuildingId, cell, GhostRotation);
                if (moved.Success)
                {
                    CarryBuildingId = null;
                    Preview = null;
                }
                return Report(moved, state, RelocateFailKey);
            }
            if (SelectedToolId != null)
            {
                return Report(PlanHistory.PlaceBeltPath(state, SelectedToolId, cell, cell, HomeGridService.BeltDirOf(GhostRotation), PendingS0, PendingS1, PendingS2), state);
            }
            if (SelectedTypeId != null)
            {
                GridOpResult r = PlanHistory.Place(state, SelectedTypeId, cell, GhostRotation, PendingS0);
                if (!r.Success)
                {
                    GuidanceHooks.Raise(GuidanceHooks.FirstBlockedPlacement);
                }
                return Report(r, state);
            }
            // 空闲状态点建筑：只显示它的信息（名字、朝向、端口、是否在搬迁），不做任何改动。
            BuildingRecord info = HomeGridService.BuildingAt(state, cell);
            // FG3-LOG-04：空闲状态点已建成的分流器 / 合流器 / 地下传送带 = 打开它的节点面板（比例、优先口、过滤、预设）。
            if (info == null && !DemolishMode && !RelocateMode && !PrioritizeMode && !ClearMode && OpenNodePanel(state, cell))
            {
                return new GridOpResult(GridOpResult.Kind.Failed, null);
            }
            // FG3-LOG-05：空闲状态点已建成的管线件 = 打开管线面板（网络读数、储罐模式、阀门、冲洗）。
            if (info == null && !DemolishMode && !RelocateMode && !PrioritizeMode && !ClearMode && OpenPipePanel(state, cell))
            {
                return new GridOpResult(GridOpResult.Kind.Failed, null);
            }
            SetStatus(info != null ? DescribeBuilding(state, info) : string.Empty, false);
            return new GridOpResult(GridOpResult.Kind.Failed, info?.BuildingId);
        }

        /// <summary>FG3-LOG-05：拆一座有存量的储罐前确认（写明会排空多少、什么流体；造价全额返还）。取消什么都不变。</summary>
        private void AskRemoveTank(GridCell cell, PipeCellInfo tank)
        {
            var req = new ConfirmRequest
            {
                Title = GameText.Get("ui.build.tank_remove_title"),
                Irreversible = true,
                ConfirmText = GameText.Get("ui.build.tank_remove_ok"),
                CancelText = GameText.Get("ui.build.confirm_cancel"),
                OnConfirm = () =>
                {
                    PendingTankConfirm = false;
                    _cells.Clear();
                    _cells.Add(cell);
                    CampaignState s = CampaignSession.Current;
                    Report(PlanHistory.RemoveCells(s, _cells), s, DemolishFailKey);
                },
                OnCancel = () => PendingTankConfirm = false,
            };
            req.Consequences.Add(GameText.Format("ui.build.tank_remove_line", (tank.TankStockMl / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                PipeNetworkService.FluidName(tank.Fluid), HomeGridService.PipePieceCost(PipePieceKind.Tank, 0)));
            UiConfirmDialog.Show(req);
            PendingTankConfirm = true;
        }

        /// <summary>FG3-LOG-05：拆储罐的确认框是否正在询问（自检用）。</summary>
        public bool PendingTankConfirm { get; private set; }

        /// <summary>搬迁模式里点起一座建筑（核心、受损、正在维修 / 拆除的不能搬，直接给原因）。朝向从它现在的朝向开始。</summary>
        private GridOpResult PickForRelocation(CampaignState state, GridCell cell)
        {
            BuildingRecord b = HomeGridService.BuildingAt(state, cell);
            if (b == null)
            {
                return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding)), state, RelocateFailKey);
            }
            if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.CannotRelocateCore)), state, RelocateFailKey);
            }
            if (b.ConstructionState == BuildingConstructionState.Damaged)
            {
                return Report(GridOpResult.Fail(GridReason.Of(GridBlockReason.RelocateDamaged)), state, RelocateFailKey);
            }
            CarryBuildingId = b.BuildingId;
            GhostRotation = GridMath.NormalizeRotation(b.Rotation);
            _previewKey = int.MinValue;
            SetStatus(GameText.Format("ui.build.relocate_carry", HomeGridService.DisplayName(b.BuildingTypeId), InputDisplay.ForAction(GameActionId.Rotate)), false);
            return new GridOpResult(GridOpResult.Kind.Failed, b.BuildingId);
        }

        /// <summary>
        /// 左键按下（鼠标与自检同一入口）：选中传送带 → 开始拖路径；拆除模式指着空地 / 传送带 → 开始拉框（指着建筑 = 单座标记）；
        /// 空闲状态指着一座建筑 → 开始“拖着搬迁”（同时显示它的信息）；其余情况等同 <see cref="ClickCell"/>。
        /// </summary>
        public void PointerDown(CampaignState state, GridCell cell)
        {
            SetHover(state, cell);
            CancelDrag();
            if (state == null)
            {
                ClickCell(state, cell);
                return;
            }
            if (SelectedToolId != null)
            {
                BeginDrag(DragKind.Belt, cell, state);
                return;
            }
            if (CopyMode)
            {
                BeginDrag(DragKind.CopyBox, cell, state);
                return;
            }
            if (UpgradeMode)
            {
                BeginDrag(DragKind.UpgradeBox, cell, state);
                return;
            }
            if (PasteMode || SettingsMode)
            {
                ClickCell(state, cell);
                return;
            }
            if (PrioritizeMode)
            {
                BeginDrag(DragKind.PrioritizeBox, cell, state);
                return;
            }
            if (ClearMode)
            {
                BeginDrag(DragKind.ClearBox, cell, state);
                return;
            }
            if (DemolishMode && HomeGridService.BuildingAt(state, cell) == null)
            {
                BeginDrag(DragKind.DemolishBox, cell, state);
                return;
            }
            string portPanelFor = null;
            if (!DemolishMode && !RelocateMode && SelectedTypeId == null)
            {
                BuildingRecord b = HomeGridService.BuildingAt(state, cell);
                if (b != null && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore)
                {
                    _dragBuildingId = b.BuildingId;
                    _dragBuildingPivot = new GridCell(b.GridX, b.GridY);
                    _dragBuildingRotation = GridMath.NormalizeRotation(b.Rotation);
                    BeginDrag(DragKind.Relocate, cell, state);
                }
                else if (b != null)
                {
                    portPanelFor = b.BuildingId; // FG3-LOG-03：核心不能拖着搬迁，点一下直接打开端口面板（其余建筑在松开且没拖动时打开）。
                }
            }
            ClickCell(state, cell);
            if (portPanelFor != null)
            {
                OpenPortPanel(state, portPanelFor);
            }
        }

        /// <summary>左键松开：按拖拽种类收尾（铺传送带 / 框选拆除 / 搬迁）。起点与终点同一格时：传送带 = 一格，框 = 点这一格，搬迁 = 什么也不做（只是点了一下）。</summary>
        public void PointerUp(CampaignState state, GridCell cell)
        {
            if (Drag == DragKind.None)
            {
                return;
            }
            SetHover(state, cell);
            DragKind kind = Drag;
            GridCell start = DragStart;
            string dragBuilding = _dragBuildingId;
            GridCell dragPivot = _dragBuildingPivot;
            int dragRot = _dragBuildingRotation;
            CancelDrag();
            if (state == null)
            {
                return;
            }
            switch (kind)
            {
                case DragKind.Belt:
                    Report(PlanHistory.PlaceBeltPath(state, SelectedToolId, start, cell, HomeGridService.BeltDirOf(GhostRotation), PendingS0, PendingS1, PendingS2), state);
                    break;
                case DragKind.CopyBox:
                    CommitCopy(state, start, cell);
                    break;
                case DragKind.UpgradeBox:
                    CommitUpgrade(state, UpgradePlanner.Plan(state, start, cell));
                    break;
                case DragKind.DemolishBox:
                    if (start == cell)
                    {
                        ClickCell(state, cell);
                        break;
                    }
                    CommitDemolishBox(state, HomeGridService.PlanDemolishBox(state, start, cell));
                    break;
                case DragKind.PrioritizeBox:
                    CommitPrioritize(state, start, cell);
                    break;
                case DragKind.ClearBox:
                    // 点一格 = 这一格所在的整条带；拖框 = 框里的传送带格。
                    CommitClear(state, start == cell ? BeltClearService.PlanNetwork(state, cell) : BeltClearService.PlanBox(state, start, cell));
                    break;
                case DragKind.Relocate:
                    // 空闲状态下拖着搬迁至少要拖出 2 格（切比雪夫距离）：点一下时手抖跨了一格边界不会误搬（要挪 1 格用搬迁模式）。
                    if (dragBuilding != null && Math.Max(Math.Abs(cell.X - start.X), Math.Abs(cell.Y - start.Y)) >= IdleDragMinCells)
                    {
                        var pivot = new GridCell(dragPivot.X + (cell.X - start.X), dragPivot.Y + (cell.Y - start.Y));
                        Report(PlanHistory.Relocate(state, dragBuilding, pivot, dragRot), state, RelocateFailKey);
                    }
                    else if (dragBuilding != null)
                    {
                        // FG3-LOG-03：点一下（没拖动）有端口的建筑 = 打开它的端口面板（接没接上、缓存、仓库输出过滤）。
                        OpenPortPanel(state, dragBuilding);
                    }
                    break;
            }
        }

        /// <summary>FG3-LOG-04：打开物流节点（分流器 / 合流器 / 地下传送带）的面板；这一格不是节点时返回 false。</summary>
        public bool OpenNodePanel(CampaignState state, GridCell cell)
        {
            if (state == null || !BeltNetworkService.TryGetNode(cell, out _))
            {
                return false;
            }
            BeltNodePanelUIToolkit.Open(cell);
            return true;
        }

        /// <summary>FG3-LOG-05：打开管线面板；这一格没有已建成的管线件时返回 false。</summary>
        public bool OpenPipePanel(CampaignState state, GridCell cell)
        {
            if (state == null || !PipeNetworkService.TryGetPiece(cell, out _, out _))
            {
                return false;
            }
            PipePanelUIToolkit.Open(cell);
            return true;
        }

        /// <summary>FG3-LOG-03：打开建筑的端口面板（没有端口的建筑不打开，状态行照旧写建筑信息）。</summary>
        public bool OpenPortPanel(CampaignState state, string buildingId)
        {
            BuildingRecord b = state != null ? HomeGridService.FindBuilding(state, buildingId) : null;
            if (b == null || GridContent.PortsOf(b.BuildingTypeId).Count == 0)
            {
                return false;
            }
            BeltPortPanelUIToolkit.Open(buildingId);
            return true;
        }

        /// <summary>
        /// FG3-LOG-03（FGR-LOG-026）：清带收尾——没有传送带 / 没有物品给提示；仓库放得下就直接送进仓库；放不下（仓库满、或家园仓库还存不了这种物品）
        /// 先弹确认框写明“能送进仓库 N 件、放不下什么”，确认才丢弃放不下的部分，取消则什么都不变。
        /// </summary>
        private void CommitClear(CampaignState state, BeltClearPlan plan)
        {
            if (!plan.HasBelts)
            {
                SetStatus(GameText.Get("ui.build.clear_no_belt"), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                LastResult = GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
                return;
            }
            if (plan.Items == 0)
            {
                SetStatus(GameText.Get("ui.build.clear_nothing"), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                LastResult = GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
                return;
            }
            if (!plan.NeedsConfirm)
            {
                ReportClear(state, plan, false);
                return;
            }
            var req = new ConfirmRequest
            {
                Title = GameText.Format("ui.build.clear_confirm_title", plan.OverflowItems),
                Irreversible = true,
                ConfirmText = GameText.Get("ui.build.clear_confirm_ok"),
                CancelText = GameText.Get("ui.build.confirm_cancel"),
                OnConfirm = () =>
                {
                    PendingClearConfirm = false;
                    ReportClear(CampaignSession.Current, plan, true);
                },
                OnCancel = () =>
                {
                    PendingClearConfirm = false;
                    LastResult = GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
                    SetStatus(GameText.Get("ui.build.clear_cancelled"), false);
                },
            };
            req.Lines.Add(GameText.Format("ui.build.clear_confirm_fit", plan.Fit));
            req.Lines.Add(GameText.Format("ui.build.clear_confirm_over", BeltItems.FormatCounts(plan.Overflow)));
            bool unstorable = false;
            foreach (KeyValuePair<ushort, int> kv in plan.Overflow)
            {
                unstorable |= !BeltItems.IsStorable(kv.Key);
            }
            req.Lines.Add(unstorable ? GameText.Get("ui.build.clear_confirm_reason_kind") : GameText.Format("ui.build.clear_confirm_reason_full", plan.FreeSpace));
            req.Consequences.Add(GameText.Get("ui.build.clear_confirm_consequence"));
            UiConfirmDialog.Show(req);
            PendingClearConfirm = true;
            LastResult = new GridOpResult(GridOpResult.Kind.Failed, null);
        }

        private void ReportClear(CampaignState state, BeltClearPlan plan, bool discard)
        {
            if (!BeltClearService.Execute(state, plan, discard, out string reasonKey))
            {
                LastResult = GridOpResult.Fail(new GridReason(GridBlockReason.Busy, reasonKey ?? "ui.build.clear_nothing"));
                SetStatus(GameText.Get(reasonKey ?? "ui.build.clear_nothing"), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return;
            }
            LastResult = new GridOpResult(GridOpResult.Kind.BeltsCleared, null);
            SetStatus(BeltClearService.LastDiscarded > 0
                ? GameText.Format("ui.build.clear_done_discard", BeltClearService.LastClearedCells, BeltClearService.LastStored, BeltClearService.LastDiscarded)
                : GameText.Format("ui.build.clear_done", BeltClearService.LastClearedCells, BeltClearService.LastStored), false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
        }

        /// <summary>空闲状态下拖着搬迁的最小拖动距离（格）。</summary>
        public const int IdleDragMinCells = 2;

        private void BeginDrag(DragKind kind, GridCell cell, CampaignState state)
        {
            Drag = kind;
            DragStart = cell;
            _dragLast = cell;
            InputRouter.BuildDragActive = true;
            RefreshDrag(state);
            Revision++;
        }

        private void CancelDrag()
        {
            if (Drag == DragKind.None && BeltPlan == null && BoxPlan == null && PrioritizeBoxCount < 0 && ClearPlan == null && UpgradePreview == null)
            {
                return;
            }
            Drag = DragKind.None;
            BeltPlan = null;
            BoxPlan = null;
            ClearPlan = null;
            UpgradePreview = null;
            PrioritizeBoxCount = -1;
            _dragBuildingId = null;
            InputRouter.BuildDragActive = false;
            Revision++;
        }

        /// <summary>拖拽终点换格时重算规划（O(路径格数) / O(建筑数)，不是每帧）。</summary>
        private void RefreshDrag(CampaignState state)
        {
            GridCell end = HasHover ? HoverCell : DragStart;
            _dragLast = end;
            switch (Drag)
            {
                case DragKind.Belt:
                    BeltPlan = HomeGridService.PlanBeltPath(state, SelectedToolId, DragStart, end, HomeGridService.BeltDirOf(GhostRotation), _beltBuffer);
                    break;
                case DragKind.DemolishBox:
                    BoxPlan = HomeGridService.PlanDemolishBox(state, DragStart, end, _boxBuffer);
                    break;
                case DragKind.ClearBox:
                    ClearPlan = end == DragStart ? BeltClearService.PlanNetwork(state, end, _clearBuffer) : BeltClearService.PlanBox(state, DragStart, end, _clearBuffer);
                    break;
                case DragKind.CopyBox:
                    CopyBoxMin = new GridCell(Math.Min(DragStart.X, end.X), Math.Min(DragStart.Y, end.Y));
                    CopyBoxMax = new GridCell(Math.Max(DragStart.X, end.X), Math.Max(DragStart.Y, end.Y));
                    break;
                case DragKind.UpgradeBox:
                    UpgradePreview = UpgradePlanner.Plan(state, DragStart, end, _upgradeBuffer);
                    break;
                case DragKind.PrioritizeBox:
                    PrioritizeBoxMin = new GridCell(Math.Min(DragStart.X, end.X), Math.Min(DragStart.Y, end.Y));
                    PrioritizeBoxMax = new GridCell(Math.Max(DragStart.X, end.X), Math.Max(DragStart.Y, end.Y));
                    PrioritizeBoxCount = HomeValleyConstruction.CountSitesInBox(state, DragStart, end);
                    break;
                case DragKind.Relocate:
                    if (_dragBuildingId != null && Math.Max(Math.Abs(end.X - DragStart.X), Math.Abs(end.Y - DragStart.Y)) >= IdleDragMinCells)
                    {
                        BuildingRecord b = HomeGridService.FindBuilding(state, _dragBuildingId);
                        if (b != null)
                        {
                            var pivot = new GridCell(_dragBuildingPivot.X + (end.X - DragStart.X), _dragBuildingPivot.Y + (end.Y - DragStart.Y));
                            // 拖的是原建筑 → 忽略它的虚影；拖的是虚影本身 → 忽略原建筑（与 RefreshPreview、MovePlan 的执行口径一致）。
                            BuildingRecord ghost = HomeGridService.FindRelocationGhost(state, b.BuildingId);
                            Preview = HomeGridService.ValidatePlacement(state, b.BuildingTypeId, pivot, _dragBuildingRotation, asPlayerPlacement: false,
                                ignoreBuildingId: b.BuildingId, checkCost: false, into: _previewBuffer, ignoreBuildingId2: ghost?.BuildingId ?? b.RelocateFromId);
                        }
                    }
                    else
                    {
                        Preview = null;
                    }
                    break;
            }
            Revision++;
        }

        /// <summary>FG3-LOG-02：“优先建造这一片”收尾——框（起点 = 终点时就是这一格）里的施工改成最高优先级，状态行写明改了几处。</summary>
        private void CommitPrioritize(CampaignState state, GridCell a, GridCell b)
        {
            int changed = HomeValleyConstruction.PrioritizeArea(state, a, b);
            LastResult = changed > 0 ? new GridOpResult(GridOpResult.Kind.Prioritized, null) : GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
            if (changed > 0)
            {
                SetStatus(GameText.Format("ui.build.prioritize_done", changed), false);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
            }
            else
            {
                SetStatus(GameText.Get("ui.build.prioritize_none"), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
            }
        }

        /// <summary>框选拆除收尾：空框给提示；超过 grid.batch_demolish_confirm 座或含关键建筑时先确认（写明数量与关键建筑名），否则直接执行。</summary>
        private void CommitDemolishBox(CampaignState state, DemolishBoxPlan plan)
        {
            if (plan.IsEmpty)
            {
                SetStatus(plan.Refused.Count > 0
                    ? GameText.Format("ui.build.box_result", 0, 0, 0, plan.Refused.Count, plan.Refused[0].Value.Describe())
                    : GameText.Get("ui.build.box_nothing"), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                LastResult = GridOpResult.Fail(plan.Refused.Count > 0 ? plan.Refused[0].Value : GridReason.Of(GridBlockReason.NoBuilding));
                return;
            }
            // FG3-LOG-07：一次拆掉的件超过撤销上限时这一步不能撤销——当作不可逆操作先确认（B04）。
            bool tooBigToUndo = plan.Belts.Count + plan.BuildingCount > PlanHistory.StepMaxEntries;
            if (!plan.NeedsConfirm && !tooBigToUndo)
            {
                ReportBatch(PlanHistory.ExecuteDemolishBox(state, plan), plan, state);
                return;
            }
            int threshold = GridContent.TuningInt("grid.batch_demolish_confirm");
            var req = new ConfirmRequest
            {
                Title = plan.BuildingCount > 0 ? GameText.Format("ui.build.batch_confirm_title", plan.BuildingCount) : GameText.Get("ui.build.box_tank_title"),
                Irreversible = true,
                ConfirmText = GameText.Get("ui.build.batch_confirm_ok"),
                CancelText = GameText.Get("ui.build.confirm_cancel"),
                OnConfirm = () =>
                {
                    PendingBatchConfirm = false;
                    CampaignState s = CampaignSession.Current;
                    ReportBatch(PlanHistory.ExecuteDemolishBox(s, plan), plan, s);
                },
                OnCancel = () => PendingBatchConfirm = false,
            };
            if (plan.BuildingCount > threshold)
            {
                req.Lines.Add(GameText.Format("ui.build.batch_confirm_line", threshold));
            }
            if (plan.CriticalNames.Count > 0)
            {
                req.Lines.Add(GameText.Format("ui.build.batch_confirm_critical", string.Join(GameText.Language == GameLanguage.En ? ", " : "、", plan.CriticalNames)));
            }
            if (plan.TankFluidMl > 0)
            {
                // FG3-LOG-05：框里的储罐有存量——拆除会排空、不返还。
                req.Lines.Add(GameText.Format("ui.build.box_tank_line", (plan.TankFluidMl / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
            }
            plan.AppendPowerLines(req.Consequences); // FG3-LOG-06：拆完会断网 / 让建筑失去电网连接时写明（按整批拆完的拓扑）。
            if (tooBigToUndo)
            {
                req.Lines.Add(GameText.Format("plan.undo.too_big_line", plan.Belts.Count + plan.BuildingCount, PlanHistory.StepMaxEntries));
            }
            req.Consequences.Add(GameText.Get("ui.build.batch_confirm_refund"));
            UiConfirmDialog.Show(req);
            PendingBatchConfirm = true;
            LastResult = new GridOpResult(GridOpResult.Kind.Failed, null);
        }

        /// <summary>框选拆除的确认框是否正在询问（自检用）。</summary>
        public bool PendingBatchConfirm { get; private set; }

        private void ReportBatch(GridOpResult r, DemolishBoxPlan plan, CampaignState state)
        {
            LastResult = r;
            if (!r.Success)
            {
                SetStatus(r.Describe(), true);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, StatusText);
                return;
            }
            string text = plan.Refused.Count > 0
                ? GameText.Format("ui.build.box_result", HomeGridService.LastBatchMarked, HomeGridService.LastBatchCancelled, HomeGridService.LastBatchBelts,
                    plan.Refused.Count, plan.Refused[0].Value.Describe())
                : GameText.Format("ui.build.box_result_ok", HomeGridService.LastBatchMarked, HomeGridService.LastBatchCancelled, HomeGridService.LastBatchBelts);
            SetStatus(text, false);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
            if (HasHover && state != null)
            {
                HoverBuildingId = HomeGridService.BuildingAt(state, HoverCell)?.BuildingId;
            }
        }

        /// <summary>拆除确认框正在询问的建筑（自检用）。</summary>
        public string PendingConfirmBuildingId { get; private set; }

        /// <summary>右键（FGR-UX-001）：取消拖拽 → 放下正在搬的建筑 → 取消选择 → 退出搬迁 / 拆除模式 → 退出建造模式。</summary>
        public void RightClick()
        {
            if (Drag != DragKind.None)
            {
                CancelDrag();
                Preview = null;
            }
            else if (CarryBuildingId != null)
            {
                CarryBuildingId = null;
                Preview = null;
                _previewKey = int.MinValue;
                Revision++;
            }
            else if (PasteMode || CopyMode || UpgradeMode || SettingsMode)
            {
                ExitPlanModes(); // FG3-LOG-07：右键退出粘贴 / 复制 / 升级 / 复制设置
                Preview = null;
                SetStatus(string.Empty, false);
            }
            else if (SelectedTypeId != null || SelectedToolId != null)
            {
                ClearSelection();
            }
            else if (PrioritizeMode)
            {
                SetPrioritizeMode(false);
            }
            else if (ClearMode)
            {
                SetClearMode(false);
            }
            else if (RelocateMode)
            {
                SetRelocateMode(false);
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

        /// <summary>FG3-LOG-08（FG-GAP-071）：飞行过渡中保持建造模式开着的帧数（自检 / 冒烟读）。</summary>
        public int FlightsKept { get; private set; }

        public void Tick(Camera camera, CampaignState state, bool inStrategyView, bool flyingToStrategy = false)
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
                    else if (InputRouter.ConsumeAction(GameActionId.RelocateMode, InputScope.Strategy))
                    {
                        SetRelocateMode(true);
                    }
                    else if (InputRouter.ConsumeAction(GameActionId.ConstructionQueue, InputScope.Strategy))
                    {
                        ConstructionQueuePanelUIToolkit.Toggle(); // FG3-LOG-02：战略视角也能打开施工队列。
                    }
                    else if (!ConsumePlanKeysClosed(camera, state))
                    {
                        ConsumeHotbarKeys(state);
                    }
                }
                return; // 本帧刚打开：下一帧起再处理鼠标（与 FG0-ARCH-04 相同，打开的那一下不会顺带放置 / 拆除）。
            }
            if (state != null && !inStrategyView && flyingToStrategy)
            {
                // FG3-LOG-08（FG-GAP-071）：镜头在家园里做一次飞行过渡、落点仍是战略视角（施工队列 / 通知 / “为什么不工作”的定位、回到核心、战略地图点哪飞哪）——
                // 建造模式保持开着（选中的工具、模式都保留），过渡期间不处理建造输入（过渡中本来就冻结玩法输入），落地后接着用。
                FlightsKept++;
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
            if (InputRouter.ConsumeAction(GameActionId.RelocateMode, InputScope.Strategy))
            {
                SetRelocateMode(!RelocateMode);
            }
            if (InputRouter.ConsumeAction(GameActionId.ToggleGridLines, InputScope.Strategy))
            {
                ToggleGridLines();
            }
            if (InputRouter.ConsumeAction(GameActionId.PrioritizeArea, InputScope.Strategy))
            {
                SetPrioritizeMode(!PrioritizeMode);
            }
            if (InputRouter.ConsumeAction(GameActionId.ClearBeltMode, InputScope.Strategy))
            {
                SetClearMode(!ClearMode);
            }
            if (InputRouter.ConsumeAction(GameActionId.ConstructionQueue, InputScope.Strategy))
            {
                ConstructionQueuePanelUIToolkit.Toggle();
            }
            ConsumeHotbarKeys(state);
            ConsumePlanKeysOpen(state);
            if (InputRouter.ConsumeAction(GameActionId.Rotate, InputScope.Strategy))
            {
                if (SelectedTypeId != null || SelectedToolId != null || CarryBuildingId != null || PasteMode)
                {
                    RotateGhost();
                    if (Drag == DragKind.Belt)
                    {
                        RefreshDrag(state);
                    }
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
                PointerDown(state, HoverCell);
            }
            if (Drag != DragKind.None && InputRouter.GetMouseButtonUp(0, InputScope.Strategy))
            {
                PointerUp(state, HasHover ? HoverCell : DragStart);
            }

            RefreshPreview(state);
            RefreshVisuals(state);
            // FG0-ARCH-05：地形叠加层跟随镜头（俯视正交镜头，焦点 = 镜头 xz）；没生成好的区块显示占位，从不在这里同步生成。
            GridCell focus = camera != null
                ? GridCell.FromWorld(new Vector2(camera.transform.position.x, camera.transform.position.z))
                : HomeGridService.CorePivot(state);
            UpdateTerrainOverlay(state, focus);
        }

        private void ConsumeHotbarKeys(CampaignState state)
        {
            for (int i = 0; i < HotbarActions.Length; i++)
            {
                if (InputRouter.ConsumeAction(HotbarActions[i], InputScope.Strategy))
                {
                    PressHotbar(state, i);
                    return;
                }
            }
        }

        /// <summary>刷新地形叠加层（窗口跟随 <paramref name="focus"/>）；画面变化（含“生成中”区块数）时 Revision+1，建造栏据此刷新。</summary>
        public void UpdateTerrainOverlay(CampaignState state, GridCell focus, bool completeNow = false)
        {
            if (_terrain == null || state == null)
            {
                return;
            }
            _terrain.GridLines = GameSettings.BuildGridLinesEnabled; // FG3-LOG-01：格线开关（变化时各区块按新值重画）。
            _terrain.PollutionView = GameLogic.View.OverlayService.Active == GameLogic.View.OverlayKind.Pollution; // FG3-LOG-08：污染叠加层。
            _terrain.Update(state, focus, completeNow);
            if (_terrain.Revision != _terrainRevision)
            {
                _terrainRevision = _terrain.Revision;
                Revision++;
                if (_terrain.PendingCount > 0)
                {
                    GuidanceHooks.Raise(GuidanceHooks.WorldFirstGenerating); // B14：只埋钩子，引导内容在 FG15-UX-04
                }
            }
        }

        /// <summary>自检用：同步补齐窗口内缺的区块并等贴图画完（正式流程从不这样做）。</summary>
        public void CompleteOverlayNow(CampaignState state, GridCell? focus = null) =>
            UpdateTerrainOverlay(state, focus ?? HomeGridService.CorePivot(state), completeNow: true);

        /// <summary>地形叠加层（未打开建造模式时为 null）。</summary>
        public WorldTerrainOverlay TerrainOverlay => _terrain;

        /// <summary>镜头周围还没生成好的区块数（建造栏显示“正在生成地形（N 个区块）…”）。</summary>
        public int GeneratingChunkCount => _terrain?.PendingCount ?? 0;

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

        /// <summary>重算虚影校验——只在（格子、朝向、选择、建筑记录、废料）变化时做，O(占地)。
        /// 选中建筑：按玩家新放置校验；搬迁模式里正搬着一座建筑：按“移动已有建筑”校验（忽略它自己与它的搬迁虚影）。</summary>
        public void RefreshPreview(CampaignState state)
        {
            RefreshToolPreview(state);
            RefreshPastePreview(state);
            if (Drag == DragKind.Relocate)
            {
                return; // 拖着搬迁的预览在 RefreshDrag 里算（跟着拖拽终点）。
            }
            string typeId = SelectedTypeId;
            BuildingRecord carry = null;
            if (typeId == null && CarryBuildingId != null && state != null)
            {
                carry = HomeGridService.FindBuilding(state, CarryBuildingId);
                typeId = carry?.BuildingTypeId;
            }
            if (typeId == null || !HasHover || state == null)
            {
                if (Preview != null)
                {
                    Preview = null;
                    Revision++;
                }
                return;
            }
            int key = HashCode(HoverCell.X, HoverCell.Y, GhostRotation, typeId.GetHashCode() ^ (carry != null ? 0x3C3C : 0));
            if (key == _previewKey && ReferenceEquals(_previewRecordsRef, state.BuildingRecords) && Mathf.Approximately(_previewScrap, state.Scrap))
            {
                return;
            }
            _previewKey = key;
            _previewRecordsRef = state.BuildingRecords;
            _previewScrap = state.Scrap;
            if (carry != null)
            {
                BuildingRecord ghost = HomeGridService.FindRelocationGhost(state, carry.BuildingId);
                string ignore2 = ghost?.BuildingId ?? carry.RelocateFromId;
                Preview = HomeGridService.ValidatePlacement(state, typeId, HoverCell, GhostRotation, asPlayerPlacement: false,
                    ignoreBuildingId: carry.BuildingId, checkCost: false, into: _previewBuffer, ignoreBuildingId2: ignore2);
            }
            else
            {
                Preview = HomeGridService.ValidatePlacement(state, typeId, HoverCell, GhostRotation, into: _previewBuffer);
            }
            if (Preview.Ok)
            {
                // FG0-ARCH-06（FGR-LOG-012 / FG03“放置会让某座建筑变得机器无法到达时，给出警告（不阻止）”）：
                // 只在格子 / 朝向 / 建筑记录变化时算一次（Burst 泛洪，家园范围内毫秒级以下）。
                if (Nav.NavService.PlacementCutsOff(state, typeId, HoverCell, GhostRotation, _cutOffScratch) > 0)
                {
                    Preview.Warnings.Add(GameText.Format("nav.build.unreachable_warning", string.Join(GameText.Language == GameLanguage.En ? ", " : "、", _cutOffScratch)));
                }
                // FG3-LOG-06（FGR-LOG-003“超出电力覆盖（只警告，不阻止）”；FG03 第 4 节“放置时预览电力覆盖”）：会接入哪个电网 / 超出覆盖的警告；电塔写会连起哪些电网、覆盖多少座建筑。
                HomeValleyPowerGrid.AppendPlacementNotes(state, Preview);
            }
            Revision++;
        }

        private readonly List<string> _cutOffScratch = new List<string>(4);

        /// <summary>
        /// FG3-LOG-04：选中工具、没在拖时的放置预览（鼠标换格 / 换朝向 / 换工具 / 废料变化时才重算，O(1)）。
        /// 地下传送带还没拖出第二格时只校验入口这一格（“要拖到出口”不算错误，提示行写怎么拖）。
        /// </summary>
        private void RefreshToolPreview(CampaignState state)
        {
            if (SelectedToolId == null || Drag != DragKind.None || !HasHover || state == null)
            {
                if (ToolPreview != null)
                {
                    ToolPreview = null;
                    _toolPreviewKey = int.MinValue;
                    Revision++;
                }
                return;
            }
            int key = HashCode(HoverCell.X, HoverCell.Y, GhostRotation, SelectedToolId.GetHashCode() ^ state.Scrap * 7919);
            if (key == _toolPreviewKey && ToolPreview != null)
            {
                return;
            }
            _toolPreviewKey = key;
            ToolPreview = HomeGridService.PlanBeltPath(state, SelectedToolId, HoverCell, HoverCell, HomeGridService.BeltDirOf(GhostRotation), _toolPreviewBuffer);
            if (ToolPreview.Kind == BeltNodeKind.UndergroundIn && ToolPreview.Reason is GridReason why && why.Code == GridBlockReason.UndergroundShape)
            {
                ToolPreview.Reason = null;
            }
            Revision++;
        }

        /// <summary>FG3-LOG-04：一个节点（或地下传送带两端）的进出口箭头（输出长箭头朝外、输入短箭头朝里，形状区分，不只靠颜色）。</summary>
        private int PlaceNodeArrows(int arrow, BeltPathPlan plan)
        {
            if (plan == null || plan.Cells.Count == 0 || plan.Dirs.Count == 0)
            {
                return arrow;
            }
            GridCell p = plan.Cells[0];
            int d = (int)plan.Dirs[0];
            if (plan.IsPipe)
            {
                // FG3-LOG-05：阀门画出进（后方）/ 出（前方）箭头；管线 / 泵 / 储罐四面相通，不画方向。
                if (plan.Pipe == PipePieceKind.Valve)
                {
                    PlaceArrow(arrow++, new PortPlacement("in", p, (GridDir)BeltDirs.Opposite(d), false));
                    PlaceArrow(arrow++, new PortPlacement("out", p, (GridDir)d, true));
                }
                return arrow;
            }
            switch (plan.Kind)
            {
                case BeltNodeKind.Splitter:
                    PlaceArrow(arrow++, new PortPlacement("in", p, (GridDir)BeltDirs.Opposite(d), false));
                    PlaceArrow(arrow++, new PortPlacement("left", p, (GridDir)BeltDirs.Left(d), true));
                    PlaceArrow(arrow++, new PortPlacement("right", p, (GridDir)BeltDirs.Right(d), true));
                    break;
                case BeltNodeKind.Merger:
                    PlaceArrow(arrow++, new PortPlacement("left", p, (GridDir)BeltDirs.Left(d), false));
                    PlaceArrow(arrow++, new PortPlacement("right", p, (GridDir)BeltDirs.Right(d), false));
                    PlaceArrow(arrow++, new PortPlacement("out", p, (GridDir)d, true));
                    break;
                case BeltNodeKind.UndergroundIn:
                    PlaceArrow(arrow++, new PortPlacement("in", p, (GridDir)BeltDirs.Opposite(d), false));
                    GridCell exit = plan.Cells[plan.Cells.Count - 1];
                    PlaceArrow(arrow++, new PortPlacement("out", exit, (GridDir)d, true));
                    break;
                default:
                    PlaceArrow(arrow++, new PortPlacement("out", p, (GridDir)d, true));
                    break;
            }
            return arrow;
        }

        private static int HashCode(int a, int b, int c, int d) => unchecked(((a * 397) ^ b) * 397 ^ c) * 397 ^ d;

        /// <summary>失败时状态行的前缀文本键：放置 = “不能放置：”，旋转 = “不能旋转：”，搬迁 = “不能搬迁：”；拆除的原因本身是完整句子，不加前缀。</summary>
        private const string PlaceFailKey = "ui.build.invalid";
        private const string RotateFailKey = "ui.build.invalid_rotate";
        private const string RelocateFailKey = "ui.build.invalid_relocate";
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
                ? HomeGridService.DisplayName(HomeGridService.FindBuilding(state, r.BuildingId)?.BuildingTypeId ?? SelectedTypeId ?? TypeOfId(r.BuildingId))
                : string.Empty;
            switch (r.Outcome)
            {
                case GridOpResult.Kind.Placed:
                    SetStatus(GameText.Format("ui.build.placed_ghost", name), false);
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
                    string cancelledType = HomeGridService.DisplayName(TypeOfId(r.BuildingId));
                    SetStatus(r.BuildingId != null && r.BuildingId.EndsWith(HomeGridService.UpgradeGhostSuffix, StringComparison.Ordinal)
                        ? GameText.Format("plan.upgrade.cancelled", cancelledType)
                        : r.BuildingId != null && r.BuildingId.EndsWith(HomeGridService.RelocationGhostSuffix, StringComparison.Ordinal)
                        ? GameText.Format("ui.build.relocate_cancelled", cancelledType)
                        : GameText.Format("ui.build.plan_cancelled", cancelledType), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.UiClick);
                    break;
                case GridOpResult.Kind.RelocationPlanned:
                    SetStatus(GameText.Format("ui.build.relocate_planned", HomeGridService.DisplayName(TypeOfId(r.BuildingId))), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
                    break;
                case GridOpResult.Kind.PlanMoved:
                    SetStatus(GameText.Format("ui.build.relocate_moved_plan", HomeGridService.DisplayName(TypeOfId(r.BuildingId))), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
                    break;
                case GridOpResult.Kind.BeltsPlaced when HomeGridService.LastPlanIsPipe:
                    SetStatus(GameText.Format("ui.build.pipe_planned", PipeNetworkService.PieceName(HomeGridService.LastPlanPipe, HomeGridService.LastPlanTier),
                        HomeGridService.LastBeltCount, HomeGridService.LastBeltScrap), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
                    break;
                case GridOpResult.Kind.BeltsRemoved when HomeGridService.LastPipesRemoved > 0 && HomeGridService.LastPipesRemoved == HomeGridService.LastBeltCount:
                    SetStatus(HomeGridService.LastPipeDrainedMl > 0
                        ? GameText.Format("ui.build.pipes_removed_drained", HomeGridService.LastPipesRemoved, HomeGridService.LastBeltScrap,
                            (HomeGridService.LastPipeDrainedMl / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                        : GameText.Format("ui.build.pipes_removed", HomeGridService.LastPipesRemoved, HomeGridService.LastBeltScrap), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.UiClick);
                    break;
                case GridOpResult.Kind.BeltsPlaced:
                    SetStatus(HomeGridService.LastPlanKind != BeltNodeKind.Belt
                        ? GameText.Format("ui.build.node_planned", BeltNetworkService.PieceName(HomeGridService.LastPlanKind, HomeGridService.LastPlanTier), HomeGridService.LastBeltScrap)
                        : GameText.Format("ui.build.belts_planned", HomeGridService.LastBeltCount, HomeGridService.LastBeltScrap), false);
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.CommandAck, StatusText);
                    break;
                case GridOpResult.Kind.BeltsRemoved:
                    SetStatus(GameText.Format("ui.build.belts_removed_full", HomeGridService.LastBeltCount, HomeGridService.LastBeltScrap,
                        HomeGridService.LastBeltItemsReturned, HomeGridService.LastPlannedBeltsCancelled), false);
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
            int move = key.IndexOf(HomeGridService.RelocationGhostSuffix, StringComparison.Ordinal);
            if (move > 0)
            {
                key = key.Substring(0, move);
            }
            int up = key.IndexOf(HomeGridService.UpgradeGhostSuffix, StringComparison.Ordinal);
            if (up > 0)
            {
                key = key.Substring(0, up);
            }
            int hash = key.IndexOf('#');
            return hash > 0 ? key.Substring(0, hash) : key;
        }

        private void SetStatus(string text, bool isError)
        {
            StatusText = text ?? string.Empty;
            StatusIsError = isError;
            Revision++;
        }

        /// <summary>建筑说明行：名字、朝向、端口（当前语言）；在搬迁的写明“正在搬迁”，搬迁目标虚影写明“搬迁目标”。</summary>
        public static string DescribeBuilding(CampaignState state, BuildingRecord b)
        {
            string text = DescribeBuilding(b);
            if (state == null)
            {
                return text;
            }
            if (HomeGridService.IsUpgradeGhost(b))
            {
                return GameText.Format("plan.upgrade.ghost_line", text);
            }
            if (HomeGridService.IsRelocationGhost(b))
            {
                return GameText.Format("ui.build.relocate_ghost", text);
            }
            if (HomeGridService.FindRelocationGhost(state, b.BuildingId) is BuildingRecord moving)
            {
                return text + "\n" + (HomeGridService.IsUpgradeGhost(moving)
                    ? GameText.Format("plan.upgrade.pending", HomeGridService.DisplayName(b.BuildingTypeId), HomeGridService.DisplayName(moving.BuildingTypeId))
                    : GameText.Format("ui.build.pending_relocation", HomeGridService.DisplayName(b.BuildingTypeId)));
            }
            // FG3-LOG-06：和电网有关的建筑写它在哪个电网、有没有电、优先级与电网读数。
            if (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled)
            {
                if (HomeValleyPowerGrid.TryDescribeBuilding(state, b, out string power))
                {
                    return text + "\n" + power;
                }
            }
            return text;
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
            _terrain = new WorldTerrainOverlay(_root.transform);
            _terrainRevision = -1;
            _cross = new GameObject("GhostCross");
            _cross.transform.SetParent(_root.transform, false);
            for (int i = 0; i < 2; i++)
            {
                GameObject bar = NewPrimitive(PrimitiveType.Cube, "Bar" + i, _cross.transform, _badMaterial);
                bar.transform.localRotation = Quaternion.Euler(0f, i == 0 ? 45f : -45f, 0f);
            }
            _cross.SetActive(false);
            for (int i = 0; i < 4; i++)
            {
                GameObject edge = NewPrimitive(PrimitiveType.Cube, "BoxEdge" + i, _root.transform, _markMaterial);
                edge.SetActive(false);
                _boxEdges.Add(edge);
            }
        }

        private GameObject NewPrimitive(PrimitiveType type, string name, Transform parent, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Collider c = go.GetComponent<Collider>();
            if (c != null)
            {
                GameLogic.View.UnityObjects.Release(c); // 不挡选中射线。
            }
            go.transform.SetParent(parent, false);
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        private void DestroyVisuals()
        {
            if (_root != null)
            {
                GameLogic.View.UnityObjects.Release(_root);
                _root = null;
            }
            _tiles.Clear();
            _arrows.Clear();
            _boxEdges.Clear();
            _marks.Clear();
            _cross = null;
            _terrain?.Dispose();
            _terrain = null;
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
                GameLogic.View.UnityObjects.Release(asset);
                asset = null;
            }
        }

        /// <summary>离开家园时调用：关闭建造模式并释放全部表现资源。</summary>
        public void Shutdown()
        {
            Close();
            DestroyVisuals();
            InputRouter.SetBuildMode(false);
            InputRouter.BuildDragActive = false;
        }

        /// <summary>按当前预览 / 拖拽 / 悬停刷新虚影格、叉形、端口箭头与框（Tick 每帧调用；自检直接调用后读 <see cref="GhostCrossVisible"/> 等）。</summary>
        public void RefreshVisuals(CampaignState state)
        {
            if (_root == null)
            {
                return;
            }
            int tile = 0;
            int arrow = 0;
            int mark = 0;
            bool showCross = false;
            bool showBox = false;
            BeltPathPlan shown = BeltPlan ?? ToolPreview;
            if (shown != null && BeltPlan == null && Preview == null)
            {
                // FG3-LOG-04：选中工具但没在拖——指着的那一格是虚影（绿 / 红），分流器 / 合流器 / 地下入口画出进出口箭头。
                PlaceTile(tile++, shown.Cells[0], shown.Ok ? _okMaterial : _badMaterial, 0.35f);
                arrow = PlaceNodeArrows(arrow, shown);
                if (!shown.Ok)
                {
                    showCross = true;
                    foreach (Transform bar in _cross.transform)
                    {
                        bar.localScale = new Vector3(1.4f, 0.15f, 0.3f);
                    }
                    _cross.transform.position = new Vector3(shown.Cells[0].X, 0.6f, shown.Cells[0].Y);
                }
            }
            else if (BeltPlan != null)
            {
                for (int i = 0; i < BeltPlan.Cells.Count && tile < 512; i++)
                {
                    bool ok = BeltPlan.Ok || (i < BeltPlan.CellOk.Count && BeltPlan.CellOk[i] && BeltPlan.FirstBadIndex != i);
                    PlaceTile(tile++, BeltPlan.Cells[i], ok && BeltPlan.Ok ? _okMaterial : _badMaterial, 0.35f);
                }
                if (BeltPlan.Kind == BeltNodeKind.UndergroundIn && BeltPlan.Cells.Count == 2)
                {
                    // FG3-LOG-04：地下段画成一串小方块（在地下，占位表现），看得出跨过哪几格。
                    int dd = (int)BeltPlan.Dirs[0];
                    for (int s = 1; s < BeltPlan.Distance && tile < 512; s++)
                    {
                        var under = new GridCell(BeltPlan.Cells[0].X + BeltDirs.Dx(dd) * s, BeltPlan.Cells[0].Y + BeltDirs.Dy(dd) * s);
                        PlaceTile(tile, under, BeltPlan.Ok ? _okMaterial : _badMaterial, 0.2f);
                        _tiles[tile].transform.localScale = new Vector3(0.3f, 0.05f, 0.3f);
                        tile++;
                    }
                }
                if (BeltPlan.Kind != BeltNodeKind.Belt)
                {
                    arrow = PlaceNodeArrows(arrow, BeltPlan);
                }
                if (!BeltPlan.Ok && BeltPlan.Cells.Count > 0)
                {
                    showCross = true;
                    GridCell bad = BeltPlan.FirstBadIndex >= 0 && BeltPlan.FirstBadIndex < BeltPlan.Cells.Count
                        ? BeltPlan.Cells[BeltPlan.FirstBadIndex]
                        : BeltPlan.Cells[BeltPlan.Cells.Count - 1];
                    foreach (Transform bar in _cross.transform)
                    {
                        bar.localScale = new Vector3(1.4f, 0.15f, 0.3f);
                    }
                    _cross.transform.position = new Vector3(bad.X, 0.6f, bad.Y);
                }
            }
            else if (BoxPlan != null)
            {
                showBox = true;
                PlaceBox(BoxPlan.Min, BoxPlan.Max);
            }
            else if (Drag == DragKind.CopyBox)
            {
                showBox = true;
                PlaceBox(CopyBoxMin, CopyBoxMax);
            }
            else if (UpgradePreview != null)
            {
                showBox = true;
                PlaceBox(UpgradePreview.Min, UpgradePreview.Max);
                tile = PlaceUpgradeTiles(state, tile);
            }
            else if (PasteMode && PastePreview != null)
            {
                tile = PlacePasteTiles(tile, out mark);
            }
            else if (Drag == DragKind.PrioritizeBox && PrioritizeBoxCount >= 0)
            {
                showBox = true;
                PlaceBox(PrioritizeBoxMin, PrioritizeBoxMax);
            }
            else if (ClearPlan != null && !ClearPlan.WholeNetwork)
            {
                showBox = true;
                PlaceBox(ClearPlan.Min, ClearPlan.Max);
            }
            else if (Preview != null)
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
            else if (HoverBuildingId != null && state != null)
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
            for (int i = mark; i < _marks.Count; i++)
            {
                _marks[i].SetActive(false);
            }
            ActiveMarkCount = mark;
            if (!showBox)
            {
                foreach (GameObject e in _boxEdges)
                {
                    e.SetActive(false);
                }
            }
            _cross.SetActive(showCross);
            ActiveTileCount = tile;
        }

        /// <summary>当前显示的虚影 / 路径格数（自检读取）。</summary>
        public int ActiveTileCount { get; private set; }

        /// <summary>框选指示器（四条边）是否可见（自检读取）。</summary>
        public bool BoxIndicatorVisible => _boxEdges.Count > 0 && _boxEdges[0].activeSelf;

        private void PlaceBox(GridCell min, GridCell max)
        {
            float x0 = min.X - 0.5f;
            float x1 = max.X + 0.5f;
            float y0 = min.Y - 0.5f;
            float y1 = max.Y + 0.5f;
            float w = x1 - x0;
            float h = y1 - y0;
            const float t = 0.2f;
            SetEdge(0, new Vector3((x0 + x1) * 0.5f, 0.5f, y0), new Vector3(w, 0.2f, t));
            SetEdge(1, new Vector3((x0 + x1) * 0.5f, 0.5f, y1), new Vector3(w, 0.2f, t));
            SetEdge(2, new Vector3(x0, 0.5f, (y0 + y1) * 0.5f), new Vector3(t, 0.2f, h));
            SetEdge(3, new Vector3(x1, 0.5f, (y0 + y1) * 0.5f), new Vector3(t, 0.2f, h));
        }

        private void SetEdge(int i, Vector3 center, Vector3 scale)
        {
            if (i >= _boxEdges.Count)
            {
                return;
            }
            GameObject e = _boxEdges[i];
            e.SetActive(true);
            e.transform.position = center;
            e.transform.localScale = scale;
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
            go.transform.localScale = new Vector3(0.9f, 0.08f, 0.9f); // FG3-LOG-04：地下段的小方块会改尺寸，池化复用时先复原。
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

        /// <summary>叠加层上某格某像素的颜色（自检读画面）；这一格的区块还在“生成中”时返回 false。</summary>
        public bool TryGetOverlayPixel(GridCell cell, int px, int py, out Color32 color)
        {
            color = default;
            return _terrain != null && _terrain.TryGetPixel(cell, px, py, out color);
        }

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
