using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>一次拖拽铺设传送带的规划（FGR-LOG-004）：路径、每格方向、每格校验、长度与成本。</summary>
    public sealed class BeltPathPlan
    {
        public string ToolId;
        public int Tier;
        /// <summary>FG3-LOG-04：这次放的是什么——传送带（拖拽路径）、分流器 / 合流器（一格）、地下传送带（<see cref="Cells"/> = [入口, 出口]）。</summary>
        public BeltNodeKind Kind;
        /// <summary>FG3-LOG-04：地下传送带入口到出口的距离（跨度 = 距离 − 1）与这一等级的最大跨度。</summary>
        public int Distance;
        public int MaxSpan;
        /// <summary>FG3-LOG-05：这是管线层的规划（管线 / 泵 / 储罐 / 阀门），<see cref="Kind"/> 无效，看 <see cref="Pipe"/>。</summary>
        public bool IsPipe;
        public PipePieceKind Pipe;
        /// <summary>FG3-LOG-05：泵脚下的流体来源（0 = 不是泵 / 不在来源上）。</summary>
        public int PipeFluid;
        /// <summary>FG3-LOG-05（FG03 第 4 节“放置时预览流体连接”）：放下后会接入哪种流体的网络（0 = 新网络 / 没有流体）。</summary>
        public int JoinFluid;
        public readonly List<GridCell> Cells = new List<GridCell>(64);
        public readonly List<BeltDir> Dirs = new List<BeltDir>(64);
        /// <summary>每格是否合法（与 <see cref="Cells"/> 一一对应，可视化逐格着色）。</summary>
        public readonly List<bool> CellOk = new List<bool>(64);
        public int ScrapPerCell;
        public int TotalCost;
        public int Stock;
        /// <summary>第一处不合法（-1 = 全部合法）与它的原因；路径超长、缺料、未解锁也写在这里。</summary>
        public int FirstBadIndex = -1;
        public GridReason? Reason;

        public int Length => Cells.Count;
        public bool Ok => Reason == null;

        /// <summary>当前语言的原因（合法时为空串）。逐格原因写成“第 N 格（x, y）：……”。</summary>
        public string Describe()
        {
            if (Reason == null)
            {
                return string.Empty;
            }
            if (FirstBadIndex >= 0 && FirstBadIndex < Cells.Count)
            {
                GridCell c = Cells[FirstBadIndex];
                return GameText.Format("ui.build.drag_bad", FirstBadIndex + 1, c.X, c.Y, Reason.Value.Describe());
            }
            return Reason.Value.Describe();
        }
    }

    /// <summary>一次框选拆除的规划（FGR-LOG-007）：框里会被影响的建筑与传送带，以及要不要先确认。</summary>
    public sealed class DemolishBoxPlan
    {
        public GridCell Min;
        public GridCell Max;
        /// <summary>已建成、会被标记拆除的建筑。</summary>
        public readonly List<string> ToMark = new List<string>();
        /// <summary>还没建成的规划（含搬迁目标虚影），会被取消并全额退回。</summary>
        public readonly List<string> ToCancel = new List<string>();
        /// <summary>不能拆的建筑与原因（核心、本局无法重建、受损、搬迁中、施工 / 维修中）。</summary>
        public readonly List<KeyValuePair<string, GridReason>> Refused = new List<KeyValuePair<string, GridReason>>();
        /// <summary>框里已经标记拆除的建筑（批量拆除不会把它们反过来“取消标记”）。</summary>
        public int AlreadyMarked;
        /// <summary>框里能拆的空传送带格与带着物品、暂时不能拆的传送带格数。</summary>
        public readonly List<GridCell> Belts = new List<GridCell>();
        public int BeltsWithItems;
        /// <summary>FG3-LOG-05：<see cref="Belts"/> 里有几格是管线层的件（含虚影），以及框里储罐的存量合计（毫升）——有存量时拆除会排空，先确认。</summary>
        public int Pipes;
        public long TankFluidMl;
        /// <summary>要拆（标记 + 取消规划）的关键建筑名（当前语言）。</summary>
        public readonly List<string> CriticalNames = new List<string>();

        public int BuildingCount => ToMark.Count + ToCancel.Count;
        public bool IsEmpty => ToMark.Count == 0 && ToCancel.Count == 0 && Belts.Count == 0;

        /// <summary>需要先确认：一次拆除超过 grid.batch_demolish_confirm 座，或其中有关键建筑，或储罐有存量，或拆完会让电网断开 / 建筑失去电网连接
        /// （FGR-LOG-007；FG00 B04；FG-GAP-086）。</summary>
        public bool NeedsConfirm => BuildingCount > GridContent.TuningInt("grid.batch_demolish_confirm") || CriticalNames.Count > 0 || TankFluidMl > 0 || PowerConsequence;

        // FG3-LOG-06：电力后果按“框里要拆的全部建筑一起拆完”的整体拓扑算；第一次读时才算（拖拽预览不付这笔）。
        private CampaignState _powerState;
        private bool _powerDone;
        private int _powerSplits;
        private int _powerOrphans;

        internal void ResetPower(CampaignState state)
        {
            _powerState = state;
            _powerDone = false;
            _powerSplits = 0;
            _powerOrphans = 0;
        }

        private void EnsurePower()
        {
            if (_powerDone)
            {
                return;
            }
            _powerDone = true;
            if (_powerState != null && ToMark.Count > 0)
            {
                HomeValleyPowerGrid.TryGetBatchRemovalImpact(_powerState, ToMark, out _powerSplits, out _powerOrphans);
            }
        }

        /// <summary>拆完后会断开的电网个数。</summary>
        public int PowerSplitGrids { get { EnsurePower(); return _powerSplits; } }
        /// <summary>拆完后失去电网连接的建筑数（不含框里要拆的）。</summary>
        public int PowerOrphans { get { EnsurePower(); return _powerOrphans; } }
        public bool PowerConsequence => PowerSplitGrids > 0 || PowerOrphans > 0;

        /// <summary>确认框里的电力后果行。</summary>
        public void AppendPowerLines(List<string> into) => HomeValleyPowerGrid.AppendBatchRemovalLines(PowerSplitGrids, PowerOrphans, into);
    }

    /// <summary>
    /// FG3-LOG-01（FGR-LOG-003、004、007、008、012；FG-GAP-015；DEBT-FG0ARCH04-03 / 12 / 13）：格网建造正式化的服务端——
    /// 出口随建筑（旋转 / 搬迁）、搬迁（组合任务）、拖拽铺设传送带、拆除传送带（全额返还）、框选批量拆除、放置时推开地面物。
    /// 与原型部分同一个唯一写入口（<see cref="HomeGridService"/>）：只改战役状态，不依赖镜头或表现对象（FGR-BASE-021）。
    /// </summary>
    public static partial class HomeGridService
    {
        /// <summary>搬迁目标虚影的 ID 后缀（原建筑 ID + 后缀）。</summary>
        public const string RelocationGhostSuffix = "@move";

        private static readonly Dictionary<string, BuildingRecord> GhostBySource = new Dictionary<string, BuildingRecord>(StringComparer.Ordinal);
        private static readonly List<GridCell> ScratchB = new List<GridCell>(64);
        private static readonly HashSet<GridCell> ScratchSet = new HashSet<GridCell>();

        // ── 占用重建辅助 ─────────────────────────────────────────────────────────

        /// <summary>从 <paramref name="cells"/> 里去掉已被 <paramref name="ownerId"/> 占着的格子（搬迁目标虚影让格）。</summary>
        private static void RemoveCellsOccupiedBy(string ownerId, List<GridCell> cells)
        {
            for (int i = cells.Count - 1; i >= 0; i--)
            {
                if (_map.OccupantAt(cells[i]) == ownerId)
                {
                    cells.RemoveAt(i);
                }
            }
        }

        /// <summary>重建占用时顺带登记“搬迁中”的原建筑 → 目标虚影（O(建筑数)，只在记录数组被替换时发生）。</summary>
        private static void IndexRelocationGhosts(BuildingRecord[] records)
        {
            GhostBySource.Clear();
            foreach (BuildingRecord b in records)
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && !string.IsNullOrEmpty(b.RelocateFromId))
                {
                    GhostBySource[b.RelocateFromId] = b;
                }
            }
        }

        /// <summary>这座建筑的搬迁目标虚影（没有在搬迁时为 null）。O(1)。</summary>
        public static BuildingRecord FindRelocationGhost(CampaignState state, string sourceBuildingId)
        {
            MapFor(state);
            return sourceBuildingId != null && GhostBySource.TryGetValue(sourceBuildingId, out BuildingRecord g) ? g : null;
        }

        // ── 出口随建筑（DEBT-FG0ARCH04-03）──────────────────────────────────────────

        /// <summary>出口锚点 → 所属建筑的开局布局行；不是出口或没写所属建筑返回 false。</summary>
        private static bool TryExitRows(StartLayout exitRow, out StartLayout ownerRow)
        {
            ownerRow = null;
            return exitRow != null && exitRow.Kind == "exit" && exitRow.OwnerAnchor != "none"
                   && GridContent.TryGetLayout(exitRow.OwnerAnchor, out ownerRow) && ownerRow.Kind == "building";
        }

        /// <summary>所属建筑以（枢轴格, 朝向）摆放时，它的出口落在哪一格：开局布局里出口相对建筑的偏移随建筑一起旋转。</summary>
        private static GridCell ExitCellFor(StartLayout exitRow, StartLayout ownerRow, GridCell pivot, int rotation)
        {
            int delta = GridMath.NormalizeRotation(rotation - ownerRow.Rotation);
            Vector2Int off = GridMath.RotateOffset(exitRow.OffsetX - ownerRow.OffsetX, exitRow.OffsetY - ownerRow.OffsetY, delta);
            return new GridCell(pivot.X + off.x, pivot.Y + off.y);
        }

        /// <summary>
        /// 出口当前所在的格子：所属建筑还在 → 按建筑现在的枢轴格与朝向推出（旋转 / 搬迁后跟着走）；所属建筑不在了 → 开局布局位置。
        /// 装配站出厂（<see cref="HomeValleyFactory"/>）与放置校验的“出口通道”障碍都读这里——箭头、出口、障碍三者永远一致。
        /// </summary>
        public static GridCell ExitCell(CampaignState state, string exitAnchorId)
        {
            StartLayout row = GridContent.Layout(exitAnchorId);
            if (TryExitRows(row, out StartLayout owner) && state != null)
            {
                BuildingRecord b = FindBuilding(state, Prefix + owner.AnchorId);
                if (b != null)
                {
                    return ExitCellFor(row, owner, new GridCell(b.GridX, b.GridY), GridMath.NormalizeRotation(b.Rotation));
                }
            }
            return AnchorCell(state, exitAnchorId);
        }

        /// <summary>出口的世界位置（格心）。</summary>
        public static Vector2 ExitPosition(CampaignState state, string exitAnchorId)
        {
            GridCell c = ExitCell(state, exitAnchorId);
            return new Vector2(c.X, c.Y);
        }

        /// <summary>正在校验的这次摆放是不是某个“带出口的开局建筑”（或它的搬迁目标虚影）：是则返回出口锚点 ID。</summary>
        private static string ExitOwnerFor(string typeId, string ignore1, string ignore2)
        {
            foreach (StartLayout row in GridContent.StartLayout)
            {
                if (!TryExitRows(row, out StartLayout owner) || owner.TypeId != typeId)
                {
                    continue;
                }
                string ownerId = Prefix + owner.AnchorId;
                if (ignore1 == ownerId || ignore2 == ownerId
                    || (ignore1 != null && ignore1 == ownerId + RelocationGhostSuffix) || (ignore2 != null && ignore2 == ownerId + RelocationGhostSuffix))
                {
                    return row.AnchorId;
                }
            }
            return null;
        }

        /// <summary>新姿态下的出口：净空方块内不能有别的建筑（忽略自己与自己的搬迁虚影），中心格必须是机器走得了的地形。</summary>
        private static void ValidateExitAt(HomeGridMap map, string exitAnchorId, GridCell pivot, int rotation, string ignore1, string ignore2,
            GridPlacementResult r)
        {
            StartLayout row = GridContent.Layout(exitAnchorId);
            if (!TryExitRows(row, out StartLayout owner))
            {
                return;
            }
            GridCell exit = ExitCellFor(row, owner, pivot, rotation);
            int radius = Mathf.FloorToInt(row.Clearance);
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var c = new GridCell(exit.X + dx, exit.Y + dy);
                    string other = map.OccupantAt(c);
                    if (other != null && other != ignore1 && other != ignore2 && !IsGhostOf(other, ignore1) && !IsGhostOf(other, ignore2))
                    {
                        string otherType = ById.TryGetValue(other, out BuildingRecord ob) ? ob.BuildingTypeId : null;
                        r.Add(new GridReason(GridBlockReason.ExitBlocked, "grid.reason.exit_blocked", otherType != null ? DisplayName(otherType) : other));
                        return;
                    }
                }
            }
            GridTerrain t = GridContent.TerrainByCode(map.GetTerrain(exit));
            if (t == null || t.NavCost == 0)
            {
                r.Add(new GridReason(GridBlockReason.ExitTerrain, "grid.reason.exit_terrain", t != null ? t.NameKey : "grid.terrain.cliff.name"));
            }
        }

        private static bool IsGhostOf(string candidate, string sourceId) =>
            sourceId != null && candidate == sourceId + RelocationGhostSuffix;

        /// <summary>出口障碍：所属建筑现在的出口；它若正在搬迁，目标位置的出口也预留（完工后机器从那里出来）。</summary>
        private static void AddExitObstacles(CampaignState state, StartLayout row, string ignore1, string ignore2)
        {
            int radius = Mathf.FloorToInt(row.Clearance);
            if (!TryExitRows(row, out StartLayout owner))
            {
                GridCell core = CorePivot(state);
                Obstacles.Add(new Obstacle(new GridCell(core.X + row.OffsetX, core.Y + row.OffsetY), radius, "grid.obstacle.exit"));
                return;
            }
            string ownerId = Prefix + owner.AnchorId;
            if (ownerId != ignore1 && ownerId != ignore2)
            {
                Obstacles.Add(new Obstacle(ExitCell(state, row.AnchorId), radius, "grid.obstacle.exit"));
            }
            if (GhostBySource.TryGetValue(ownerId, out BuildingRecord ghost) && ghost.BuildingId != ignore1 && ghost.BuildingId != ignore2
                && ownerId != ignore1 && ownerId != ignore2)
            {
                Obstacles.Add(new Obstacle(ExitCellFor(row, owner, new GridCell(ghost.GridX, ghost.GridY), GridMath.NormalizeRotation(ghost.Rotation)),
                    radius, "grid.obstacle.exit"));
            }
        }

        // ── 搬迁（FGR-LOG-008）─────────────────────────────────────────────────────

        /// <summary>
        /// 把一座建筑搬到（枢轴格, 朝向）：
        /// - 还没开工的规划（含搬迁目标虚影；工作单还在待分配 / 等待）→ 直接挪过去，不花钱（<see cref="GridOpResult.Kind.PlanMoved"/>）；
        /// - 已建成的建筑 → 生成“拆除原建筑 + 在新位置放虚影”的组合任务：新位置出现搬迁目标虚影与一张施工工作单（材料 0，
        ///   工期 grid.relocate_seconds），机器施工完成那一刻原建筑移除、虚影接过原建筑的 ID 与全部设置（生命、库存、队列、
        ///   电力优先级、投入）。完工前原建筑照常运转；点新位置的虚影可以取消搬迁，原建筑不受影响（<see cref="GridOpResult.Kind.RelocationPlanned"/>）；
        /// - 已经在搬迁的建筑再搬一次 = 把它的目标虚影挪到新位置。
        /// 新位置按“移动已有建筑”校验（不查数量上限与成本，忽略自己；可以与原位置重叠——重叠的格子在完工前归原建筑）。
        /// 拒绝：核心、受损、正在维修 / 拆除、施工中的规划、位置没变、新位置非法（逐条原因同放置）。
        /// 本局无法重建（placeable=0）的开局建筑**可以**搬迁：原建筑直到新位置完工才移除，不会软锁。
        /// </summary>
        public static GridOpResult TryRelocate(CampaignState state, string buildingId, GridCell pivot, int rotation)
        {
            BuildingRecord b = FindBuilding(state, buildingId);
            if (b == null)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
            }
            if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.CannotRelocateCore));
            }
            int rot = GridMath.NormalizeRotation(rotation);

            // 还没开工的规划：直接挪。
            if (b.ConstructionState == BuildingConstructionState.Planned || b.ConstructionState == BuildingConstructionState.MaterialReserved
                || b.ConstructionState == BuildingConstructionState.Building)
            {
                return MovePlan(state, b, pivot, rot);
            }

            BuildingRecord ghost = FindRelocationGhost(state, b.BuildingId);
            if (ghost != null)
            {
                return MovePlan(state, ghost, pivot, rot);
            }
            if (b.ConstructionState == BuildingConstructionState.Damaged)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.RelocateDamaged));
            }
            if (b.ConstructionState != BuildingConstructionState.Operational
                || HasActiveOrder(state, b.BuildingId, WorkOrderKind.Repair) || HasActiveOrder(state, b.BuildingId, WorkOrderKind.Salvage))
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.Busy));
            }
            if (pivot.X == b.GridX && pivot.Y == b.GridY && rot == GridMath.NormalizeRotation(b.Rotation))
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.RelocateSame));
            }
            GridPlacementResult check = ValidatePlacement(state, b.BuildingTypeId, pivot, rot, asPlayerPlacement: false,
                ignoreBuildingId: b.BuildingId, checkCost: false);
            if (!check.Ok)
            {
                return GridOpResult.Fail(check.Reasons[0], check);
            }
            BuildingGrid g = GridContent.Building(b.BuildingTypeId);
            string ghostId = b.BuildingId + RelocationGhostSuffix;
            Vector2 center = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, rot);
            HomeValleyWorkOrders.WorkOrderOpResult order = HomeValleyWorkOrders.TryCreateRelocationAt(state, b, ghostId, pivot, rot, center,
                GridContent.Tuning("grid.relocate_seconds"));
            if (!order.Success)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.Busy), check);
            }
            MapFor(state);
            ClaimFootprint(state, check.Cells);
            Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstRelocate);
            return new GridOpResult(GridOpResult.Kind.RelocationPlanned, ghostId, check);
        }

        /// <summary>把一条还没开工的规划挪到新位置（不花钱、不改工作单，机器按新位置去施工）。</summary>
        private static GridOpResult MovePlan(CampaignState state, BuildingRecord plan, GridCell pivot, int rot)
        {
            WorkOrderRecord build = FindActive(state, plan.BuildingId, WorkOrderKind.Build);
            if (build == null || build.State == WorkOrderState.InProgress || plan.ConstructionState == BuildingConstructionState.Building)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.RelocateUnderConstruction));
            }
            if (pivot.X == plan.GridX && pivot.Y == plan.GridY && rot == GridMath.NormalizeRotation(plan.Rotation))
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.RelocateSame));
            }
            GridPlacementResult check = ValidatePlacement(state, plan.BuildingTypeId, pivot, rot, asPlayerPlacement: false,
                ignoreBuildingId: plan.BuildingId, checkCost: false, ignoreBuildingId2: plan.RelocateFromId);
            if (!check.Ok)
            {
                return GridOpResult.Fail(check.Reasons[0], check);
            }
            BuildingGrid g = GridContent.Building(plan.BuildingTypeId);
            plan.GridX = pivot.X;
            plan.GridY = pivot.Y;
            plan.Rotation = rot;
            plan.Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, rot);
            // 记录数组换一份（内容相同）→ 占用层整体重建（与“占用只在记录变化时重建”同一条路径，不另写增量逻辑）。
            state.BuildingRecords = (BuildingRecord[])state.BuildingRecords.Clone();
            HomeValleyWorkOrders.OnPlanMoved(state, build);
            MapFor(state);
            ClaimFootprint(state, check.Cells);
            return new GridOpResult(GridOpResult.Kind.PlanMoved, plan.BuildingId, check);
        }

        /// <summary>搬迁完工：原建筑的新占地落定（工单完工时调用）。</summary>
        public static void OnRelocationCompleted(CampaignState state, BuildingRecord moved)
        {
            var cells = new List<GridCell>(25);
            FootprintOf(moved, cells);
            ClaimFootprint(state, cells);
        }

        /// <summary>这条记录是不是还在施工前的搬迁目标虚影。</summary>
        public static bool IsRelocationGhost(BuildingRecord b) => b != null && !string.IsNullOrEmpty(b.RelocateFromId);

        // ── 地面物让位（FG-GAP-015）───────────────────────────────────────────────

        /// <summary>FG-GAP-015：新占地落定（放置虚影、旋转、搬迁目标、搬迁完工）时广播占地格。家园地点载入时订阅，
        /// 把站在这些格子上的机器挪到最近的空地（机器位置在战斗内核里，本服务不直接碰内核）。</summary>
        public static event Action<CampaignState, List<GridCell>> FootprintClaimed;

        /// <summary>最近一次让位时挪走的机器数（自检读取）。</summary>
        public static int LastMachinesPushed { get; set; }

        /// <summary>最近的让位空格：不在 <paramref name="avoid"/> 里、没有建筑 / 传送带、机器走得了的地形；最多向外找 <paramref name="maxRing"/> 圈。</summary>
        public static bool TryFindFreeCellNear(CampaignState state, GridCell at, HashSet<GridCell> avoid, int maxRing, out GridCell free)
        {
            HomeGridMap map = MapFor(state);
            for (int ring = 1; ring <= maxRing; ring++)
            {
                for (int dy = -ring; dy <= ring; dy++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        {
                            continue;
                        }
                        var c = new GridCell(at.X + dx, at.Y + dy);
                        if ((avoid != null && avoid.Contains(c)) || map.OccupantAt(c) != null || map.GetBelt(c) != 0)
                        {
                            continue;
                        }
                        GridTerrain t = GridContent.TerrainByCode(map.GetTerrain(c));
                        if (t == null || t.NavCost == 0)
                        {
                            continue;
                        }
                        free = c;
                        return true;
                    }
                }
            }
            free = at;
            return false;
        }

        /// <summary>
        /// 压在 <paramref name="footprint"/> 上的家园地面物挪到最近的空地（不在占地里、没有建筑 / 传送带、机器走得了的地形），
        /// 最多向外找 grid.start_protect_radius 圈；找不到就留在原地（不会消失，机器照样能搬）。开销 O(地面物数)，只在放置 / 搬迁时发生。
        /// </summary>
        public static int PushGroundItemsOut(CampaignState state, List<GridCell> footprint)
        {
            if (state?.GroundItems == null || state.GroundItems.Length == 0 || footprint == null || footprint.Count == 0)
            {
                return 0;
            }
            HomeGridMap map = MapFor(state);
            ScratchSet.Clear();
            foreach (GridCell c in footprint)
            {
                ScratchSet.Add(c);
            }
            int moved = 0;
            int maxRing = Math.Max(2, GridContent.TuningInt("grid.start_protect_radius"));
            foreach (GroundItemRecord item in state.GroundItems)
            {
                if (item == null || item.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                GridCell at = GridCell.FromWorld(item.Position);
                if (ScratchSet.Contains(at) && TryFindFreeCellNear(state, at, ScratchSet, maxRing, out GridCell free))
                {
                    item.Position = new Vector2(free.X, free.Y);
                    moved++;
                }
            }
            LastGroundItemsPushed = moved;
            return moved;
        }

        /// <summary>最近一次让位时挪走的地面物数（自检读取）。</summary>
        public static int LastGroundItemsPushed { get; private set; }

        /// <summary>新占地落定：挪开地面物，并通知家园地点挪开站在上面的机器（FG-GAP-015）。</summary>
        private static void ClaimFootprint(CampaignState state, List<GridCell> footprint)
        {
            PushGroundItemsOut(state, footprint);
            LastMachinesPushed = 0;
            FootprintClaimed?.Invoke(state, footprint);
        }

        // ── 传送带：拖拽铺设与拆除（FGR-LOG-004、007）────────────────────────────────

        /// <summary>从 <paramref name="from"/> 到 <paramref name="to"/> 的传送带路径（自动转角）：先走相差更多的那个轴，再走另一个轴；
        /// 每格方向指向下一格，最后一格沿用前一段的方向；只有一格时方向 = <paramref name="singleDir"/>。</summary>
        public static void BuildBeltPath(GridCell from, GridCell to, BeltDir singleDir, List<GridCell> cells, List<BeltDir> dirs)
        {
            cells.Clear();
            dirs.Clear();
            int dx = to.X - from.X;
            int dy = to.Y - from.Y;
            if (dx == 0 && dy == 0)
            {
                cells.Add(from);
                dirs.Add(singleDir);
                return;
            }
            bool xFirst = Math.Abs(dx) >= Math.Abs(dy);
            var c = from;
            cells.Add(c);
            void Walk(int steps, int sx, int sy)
            {
                for (int i = 0; i < steps; i++)
                {
                    c = new GridCell(c.X + sx, c.Y + sy);
                    cells.Add(c);
                }
            }
            if (xFirst)
            {
                Walk(Math.Abs(dx), Math.Sign(dx), 0);
                Walk(Math.Abs(dy), 0, Math.Sign(dy));
            }
            else
            {
                Walk(Math.Abs(dy), 0, Math.Sign(dy));
                Walk(Math.Abs(dx), Math.Sign(dx), 0);
            }
            for (int i = 0; i < cells.Count; i++)
            {
                GridCell a = i + 1 < cells.Count ? cells[i] : cells[i - 1];
                GridCell b = i + 1 < cells.Count ? cells[i + 1] : cells[i];
                dirs.Add(DirBetween(a, b));
            }
        }

        private static BeltDir DirBetween(GridCell a, GridCell b)
        {
            if (b.X > a.X)
            {
                return BeltDir.East;
            }
            if (b.X < a.X)
            {
                return BeltDir.West;
            }
            return b.Y > a.Y ? BeltDir.North : BeltDir.South;
        }

        /// <summary>朝向（0 / 90 / 180 / 270，俯视顺时针，0 = 北）→ 传送带方向。</summary>
        public static BeltDir BeltDirOf(int rotation) => (BeltDir)GridMath.RotationSteps(rotation);

        /// <summary>
        /// 规划一次拖拽铺设：逐格按格网规则校验（与单格铺设同一套：迷雾、地形、污染、建筑 / 传送带 / 管线占用、开局锚点），
        /// 再查长度上限、解锁、总成本与库存。O(路径格数)，只在拖拽终点换格时重算。
        /// </summary>
        /// <summary>FG3-LOG-04：建造菜单工具种类 → 物流件种类（传送带 / 分流器 / 合流器 / 地下传送带；认不出来返回 false）。</summary>
        public static bool TryToolKind(string toolKind, out BeltNodeKind kind)
        {
            switch (toolKind)
            {
                case "belt":
                    kind = BeltNodeKind.Belt;
                    return true;
                case "splitter":
                    kind = BeltNodeKind.Splitter;
                    return true;
                case "merger":
                    kind = BeltNodeKind.Merger;
                    return true;
                case "underground":
                    kind = BeltNodeKind.UndergroundIn;
                    return true;
                default:
                    kind = BeltNodeKind.Belt;
                    return false;
            }
        }

        public static BeltPathPlan PlanBeltPath(CampaignState state, string toolId, GridCell from, GridCell to, BeltDir singleDir, BeltPathPlan into = null)
        {
            BeltPathPlan plan = into ?? new BeltPathPlan();
            plan.ToolId = toolId;
            plan.CellOk.Clear();
            plan.FirstBadIndex = -1;
            plan.Reason = null;
            plan.TotalCost = 0;
            plan.Distance = 0;
            plan.MaxSpan = 0;
            plan.Kind = BeltNodeKind.Belt;
            plan.Stock = state != null ? state.Scrap : 0;
            plan.IsPipe = false;
            plan.PipeFluid = 0;
            plan.JoinFluid = 0;
            if (GridContent.TryGetTool(toolId, out BuildTool pipeTool) && PipeNetworkService.TryToolPiece(pipeTool.Kind, out PipePieceKind piece))
            {
                return PlanPipePath(state, pipeTool, piece, from, to, singleDir, plan);
            }
            if (!GridContent.TryGetTool(toolId, out BuildTool tool) || !TryToolKind(tool.Kind, out BeltNodeKind kind))
            {
                BuildBeltPath(from, to, singleDir, plan.Cells, plan.Dirs);
                plan.Reason = GridReason.Of(GridBlockReason.UnknownType);
                return plan;
            }
            plan.Kind = kind;
            if (kind == BeltNodeKind.Splitter || kind == BeltNodeKind.Merger)
            {
                // FG3-LOG-04：分流器 / 合流器一次放一座：放在松开鼠标的那一格，朝向 = 旋转键设定的方向。
                plan.Cells.Clear();
                plan.Dirs.Clear();
                plan.Cells.Add(to);
                plan.Dirs.Add(singleDir);
            }
            else if (kind == BeltNodeKind.UndergroundIn)
            {
                // FG3-LOG-04（FGR-LOG-023）：地下传送带 = 入口（按下的格）→ 出口（松开的格），沿直线，入口朝拖动方向。
                plan.Cells.Clear();
                plan.Dirs.Clear();
                BeltDir d = from == to ? singleDir : DirBetween(from, to);
                plan.Cells.Add(from);
                plan.Dirs.Add(d);
                if (to != from)
                {
                    plan.Cells.Add(to);
                    plan.Dirs.Add(d);
                }
                plan.Distance = Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y);
                plan.MaxSpan = BeltNetworkService.UndergroundSpan(tool.Tier);
            }
            else
            {
                BuildBeltPath(from, to, singleDir, plan.Cells, plan.Dirs);
            }
            plan.Tier = tool.Tier;
            plan.ScrapPerCell = tool.ScrapPerCell;
            // 地下传送带按两端计价（每端 scrapPerCell），没拖出第二格时也按一整条（两端）显示成本。
            plan.TotalCost = tool.ScrapPerCell * (kind == BeltNodeKind.UndergroundIn ? 2 : plan.Cells.Count);
            if (state == null)
            {
                plan.Reason = GridReason.Of(GridBlockReason.NoRegion);
                return plan;
            }
            if (!BuildCatalog.IsUnlocked(state, tool.UnlockRule))
            {
                plan.Reason = new GridReason(GridBlockReason.ToolLocked, "grid.reason.not_unlocked_tool", tool.UnlockHintKey);
                return plan;
            }
            int max = GridContent.TuningInt("grid.drag_max_cells");
            if (plan.Cells.Count > max)
            {
                plan.Reason = new GridReason(GridBlockReason.DragTooLong, "grid.reason.drag_too_long", max.ToString());
                for (int i = 0; i < plan.Cells.Count; i++)
                {
                    plan.CellOk.Add(i < max);
                }
                return plan;
            }
            GridPlacementResult cellCheck = new GridPlacementResult();
            for (int i = 0; i < plan.Cells.Count; i++)
            {
                ValidateBeltCell(state, plan.Cells[i], cellCheck);
                plan.CellOk.Add(cellCheck.Ok);
                if (!cellCheck.Ok && plan.FirstBadIndex < 0)
                {
                    plan.FirstBadIndex = i;
                    plan.Reason = cellCheck.Reasons[0];
                }
            }
            if (plan.Kind == BeltNodeKind.UndergroundIn && plan.Reason == null)
            {
                CheckUndergroundShape(state, plan, from, to);
            }
            // FG3-LOG-02（FGR-LOG-003 / 006）：库存不够不再拦截拖拽——放下的是虚影，机器取料施工，缺料就等（HUD 仍写“还差 Z”）。
            return plan;
        }

        /// <summary>
        /// FG3-LOG-05（FGR-LOG-040～043；DEBT-FG3LOG01-03 管线拖拽）：规划管线层的放置——管线按住拖拽（与传送带同一条自动转角路径），泵 / 储罐 / 阀门一次一件（放在松开的格，
        /// 阀门流向 = 旋转键设定的方向）。逐格按 <see cref="ValidatePipeCell"/>（占用、迷雾、地形、污染、泵要在水源 / 油井上）校验；再查流体规则：
        /// 这一段会相连的各网络（加上泵自己的来源）最多一种流体，阀门两侧流体相同或有一侧没有——否则整段拒绝并写明是哪两种流体（全有或全无）。
        /// </summary>
        private static BeltPathPlan PlanPipePath(CampaignState state, BuildTool tool, PipePieceKind piece, GridCell from, GridCell to, BeltDir singleDir, BeltPathPlan plan)
        {
            plan.IsPipe = true;
            plan.Pipe = piece;
            plan.Kind = BeltNodeKind.Belt;
            if (piece == PipePieceKind.Pipe)
            {
                BuildBeltPath(from, to, singleDir, plan.Cells, plan.Dirs);
            }
            else
            {
                plan.Cells.Clear();
                plan.Dirs.Clear();
                plan.Cells.Add(to);
                plan.Dirs.Add(singleDir);
            }
            plan.Tier = piece == PipePieceKind.Pipe ? Math.Max(0, Math.Min(PipeConst.TierCount - 1, tool.Tier)) : 0;
            plan.ScrapPerCell = tool.ScrapPerCell;
            plan.TotalCost = tool.ScrapPerCell * plan.Cells.Count;
            if (state == null)
            {
                plan.Reason = GridReason.Of(GridBlockReason.NoRegion);
                return plan;
            }
            if (!BuildCatalog.IsUnlocked(state, tool.UnlockRule))
            {
                plan.Reason = new GridReason(GridBlockReason.ToolLocked, "grid.reason.not_unlocked_tool", tool.UnlockHintKey);
                return plan;
            }
            int max = GridContent.TuningInt("grid.drag_max_cells");
            if (plan.Cells.Count > max)
            {
                plan.Reason = new GridReason(GridBlockReason.DragTooLong, "grid.reason.drag_too_long", max.ToString());
                for (int i = 0; i < plan.Cells.Count; i++)
                {
                    plan.CellOk.Add(i < max);
                }
                return plan;
            }
            GridPlacementResult cellCheck = new GridPlacementResult();
            for (int i = 0; i < plan.Cells.Count; i++)
            {
                ValidatePipeCell(state, plan.Cells[i], piece, cellCheck);
                plan.CellOk.Add(cellCheck.Ok);
                if (!cellCheck.Ok && plan.FirstBadIndex < 0)
                {
                    plan.FirstBadIndex = i;
                    plan.Reason = cellCheck.Reasons[0];
                }
            }
            if (plan.Reason != null || !PipeNetworkService.IsRunning)
            {
                return plan;
            }
            if (piece == PipePieceKind.Pump)
            {
                plan.PipeFluid = PipeNetworkService.SourceFluidAt(state, plan.Cells[0]);
            }
            if (piece == PipePieceKind.Valve)
            {
                PipeResult vr = PipeNetworkService.CheckValve(plan.Cells[0], (int)plan.Dirs[0], out int va, out int vb);
                if (vr == PipeResult.FluidConflict)
                {
                    plan.Reason = new GridReason(GridBlockReason.PipeFluidConflict, "logistics.pipe.reason.valve_conflict",
                        PipeNetworkService.FluidName(va), PipeNetworkService.FluidName(vb));
                    plan.FirstBadIndex = 0;
                    plan.CellOk[0] = false;
                }
                else if (vr == PipeResult.ValveChained)
                {
                    plan.Reason = GridReason.Of(GridBlockReason.PipeValveChained);
                    plan.FirstBadIndex = 0;
                    plan.CellOk[0] = false;
                }
                return plan;
            }
            PipeFluidScratch.Clear();
            if (plan.PipeFluid > 0)
            {
                PipeFluidScratch.Add(plan.PipeFluid);
            }
            for (int i = 0; i < plan.Cells.Count; i++)
            {
                PipeNetworkService.AddAdjacentFluids(plan.Cells[i], piece, 0, PipeFluidScratch);
                if (PipeFluidScratch.Count > 1)
                {
                    plan.Reason = new GridReason(GridBlockReason.PipeFluidConflict, "grid.reason.pipe_fluid_conflict",
                        PipeNetworkService.FluidName(PipeFluidScratch[0]), PipeNetworkService.FluidName(PipeFluidScratch[1]));
                    plan.FirstBadIndex = i;
                    plan.CellOk[i] = false;
                    break;
                }
            }
            plan.JoinFluid = PipeFluidScratch.Count > 0 ? PipeFluidScratch[0] : 0;
            return plan;
        }

        private static readonly List<int> PipeFluidScratch = new List<int>(4);

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-023；负向“地下带跨度超限”）：地下传送带的形状——要拖出第二格、入口与出口在同一行 / 列、跨度不超过这一等级的上限、
        /// 同方向的地下段（已建成的，或还是虚影的地下传送带规划）不能重叠。原因写明上限与这次的跨度。
        /// </summary>
        private static void CheckUndergroundShape(CampaignState state, BeltPathPlan plan, GridCell from, GridCell to)
        {
            if (from == to)
            {
                plan.Reason = new GridReason(GridBlockReason.UndergroundShape, "grid.reason.under_same_cell");
                return;
            }
            if (from.X != to.X && from.Y != to.Y)
            {
                plan.Reason = new GridReason(GridBlockReason.UndergroundShape, "grid.reason.under_not_straight");
                plan.FirstBadIndex = 1;
                return;
            }
            string toolName = GridContent.TryGetTool(plan.ToolId, out BuildTool tool) ? tool.NameKey : "logistics.underground.t1";
            if (plan.Distance - 1 > plan.MaxSpan)
            {
                plan.Reason = new GridReason(GridBlockReason.UndergroundSpan, "grid.reason.under_too_far", toolName,
                    plan.MaxSpan.ToString(System.Globalization.CultureInfo.InvariantCulture), (plan.Distance - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                plan.FirstBadIndex = 1;
                return;
            }
            if (TryFindUndergroundOverlap(state, from, plan.Dirs[0], plan.Distance, out GridCell at))
            {
                plan.Reason = new GridReason(GridBlockReason.UndergroundOccupied, "grid.reason.under_occupied",
                    at.X.ToString(System.Globalization.CultureInfo.InvariantCulture), at.Y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// 从入口 <paramref name="entrance"/> 朝 <paramref name="dir"/> 距离 <paramref name="distance"/> 的地下段，是否与同方向的地下段重叠——
        /// 已建成的（内核地下层）或还是虚影的地下传送带规划（南北向与东西向互不影响）。O(跨度 + 地下规划数 × 跨度)，只在拖拽换格时。
        /// </summary>
        public static bool TryFindUndergroundOverlap(CampaignState state, GridCell entrance, BeltDir dir, int distance, out GridCell at)
        {
            at = default;
            int d = (int)dir;
            bool ns = (d & 1) == 0;
            for (int s = 1; s < distance; s++)
            {
                int x = entrance.X + BeltDirs.Dx(d) * s;
                int y = entrance.Y + BeltDirs.Dy(d) * s;
                if (BeltNetworkService.IsRunning && BeltNetworkService.Kernel.HasCell(x, BeltDirs.UnderY(y, d)))
                {
                    at = new GridCell(x, y);
                    return true;
                }
            }
            foreach (PlannedBeltRecord p in state?.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                if (p == null || p.NodeKind != (int)BeltNodeKind.UndergroundIn || p.Xs == null || p.Xs.Length < 2 || p.CellState[0] != 0
                    || ((p.Dirs[0] & 1) == 0) != ns)
                {
                    continue;
                }
                int pd = p.Dirs[0];
                int pdist = Math.Abs(p.Xs[1] - p.Xs[0]) + Math.Abs(p.Ys[1] - p.Ys[0]);
                for (int s = 1; s < distance; s++)
                {
                    int x = entrance.X + BeltDirs.Dx(d) * s;
                    int y = entrance.Y + BeltDirs.Dy(d) * s;
                    // 同一条直线上、落在那份规划的地下段（1 .. 距离−1）里。
                    int k = ns ? (x == p.Xs[0] ? (y - p.Ys[0]) * BeltDirs.Dy(pd) : -1) : (y == p.Ys[0] ? (x - p.Xs[0]) * BeltDirs.Dx(pd) : -1);
                    if (k >= 1 && k < pdist)
                    {
                        at = new GridCell(x, y);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 按规划放下传送带虚影（全有或全无：任何一格不合法、超长、未解锁就一格都不放，状态不变）。
        /// FG3-LOG-02（DEBT-FG3LOG01-01 关闭）：不再“放下即成、一次扣清”——生成一份传送带规划与一张施工单，机器从仓库取料、走到现场，
        /// 按路径顺序一格一格建成进内核；材料不够也能放，虚影等材料。战略暂停中照样可以规划，恢复后才施工。
        /// </summary>
        public static GridOpResult TryPlaceBeltPath(CampaignState state, string toolId, GridCell from, GridCell to, BeltDir singleDir)
        {
            BeltPathPlan plan = PlanBeltPath(state, toolId, from, to, singleDir);
            if (!plan.Ok)
            {
                if (plan.Reason.Value.Code == GridBlockReason.PipeFluidConflict)
                {
                    Core.GuidanceHooks.Raise(Core.GuidanceHooks.LogisticsPipeFirstFluidConflict);
                }
                return GridOpResult.Fail(plan.Reason.Value);
            }
            if (plan.IsPipe ? !PipeNetworkService.IsRunning : !BeltNetworkService.IsRunning)
            {
                return GridOpResult.Fail(new GridReason(GridBlockReason.Busy, plan.IsPipe ? "logistics.pipe.reason.not_running" : "logistics.reason.not_running"));
            }
            if (plan.IsPipe && PipeNetworkService.SavedDataPreserved)
            {
                return GridOpResult.Fail(new GridReason(GridBlockReason.Busy, "logistics.pipe.reason.save_preserved"));
            }
            HomeValleyConstruction.PlanBelts(state, plan);
            if (plan.Cells.Count > 1)
            {
                Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstDrag);
            }
            Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstGhost);
            LastBeltCount = plan.Cells.Count;
            LastBeltScrap = plan.TotalCost;
            LastPlanKind = plan.Kind;
            LastPlanTier = plan.Tier;
            LastPlanIsPipe = plan.IsPipe;
            LastPlanPipe = plan.Pipe;
            return new GridOpResult(GridOpResult.Kind.BeltsPlaced, null);
        }

        /// <summary>最近一次铺设 / 拆除传送带的格数与废料（状态行显示用）。</summary>
        public static int LastBeltCount { get; private set; }
        public static int LastBeltScrap { get; private set; }
        /// <summary>FG3-LOG-04：最近一次放下的是什么（传送带 / 分流器 / 合流器 / 地下传送带）与等级（状态行写“已放下分流器的虚影”）。</summary>
        public static BeltNodeKind LastPlanKind { get; private set; }
        /// <summary>FG3-LOG-05：最近一次放下的是不是管线层的件，以及是哪一种（状态行写“已放下管线 T1 的虚影 N 格”）。</summary>
        public static bool LastPlanIsPipe { get; private set; }
        public static PipePieceKind LastPlanPipe { get; private set; }
        public static int LastPlanTier { get; private set; }

        /// <summary>
        /// FG3-LOG-04：一件物流件的造价（建造菜单里对应工具的 scrapPerCell）：传送带按等级每格；分流器 / 合流器每座；地下传送带每端（整条 = 两端）。
        /// 菜单里没有对应工具时为 0。
        /// </summary>
        public static int PieceCost(BeltNodeKind kind, int tier)
        {
            string toolKind = kind == BeltNodeKind.Splitter ? "splitter" : kind == BeltNodeKind.Merger ? "merger"
                : kind == BeltNodeKind.UndergroundIn || kind == BeltNodeKind.UndergroundOut ? "underground" : "belt";
            bool byTier = toolKind == "belt" || toolKind == "underground";
            foreach (BuildTool t in GridContent.Tools)
            {
                if (t.Kind == toolKind && (!byTier || t.Tier == tier))
                {
                    return t.ScrapPerCell;
                }
            }
            return 0;
        }

        /// <summary>FG3-LOG-05：一件管线层的件的造价（建造菜单里对应工具的 scrapPerCell；管线按等级）。拆除全额返还同一个数。</summary>
        public static int PipePieceCost(PipePieceKind kind, int tier)
        {
            string toolKind = kind == PipePieceKind.Pump ? "pump" : kind == PipePieceKind.Tank ? "tank" : kind == PipePieceKind.Valve ? "valve" : "pipe";
            foreach (BuildTool t in GridContent.Tools)
            {
                if (t.Kind == toolKind && (toolKind != "pipe" || t.Tier == tier))
                {
                    return t.ScrapPerCell;
                }
            }
            return 0;
        }

        /// <summary>FG3-LOG-04：拆掉一件已建成的物流件返还多少（地下传送带拆任一端 = 整条 = 两端）。</summary>
        public static int PieceRefund(BeltNodeKind kind, int tier) =>
            PieceCost(kind, tier) * (kind == BeltNodeKind.UndergroundIn || kind == BeltNodeKind.UndergroundOut ? 2 : 1);

        /// <summary>某一等级传送带每格的造价（建造菜单里对应工具的 scrapPerCell；菜单里没有这个等级时为 0）。</summary>
        public static int BeltCostPerCell(int tier)
        {
            foreach (BuildTool t in GridContent.Tools)
            {
                if (t.Kind == "belt" && t.Tier == tier)
                {
                    return t.ScrapPerCell;
                }
            }
            return 0;
        }

        /// <summary>
        /// 拆一批传送带格（FGR-LOG-007 全额返还）：
        /// - 规划中（虚影）的格子：取消规划（<see cref="HomeValleyConstruction.CancelPlannedCells"/>），运到现场多出来的材料退回；
        /// - 已建成的格子：造价全额返还；FG3-LOG-02（DEBT-FG3LOG01-02 / DEBT-FG0ARCH02-03 的返还部分）起**带上的物品一并返还**，不再拒拆——
        ///   物品按种类落在拆除处（家园仓库暂时只存废料，其它物品留在地上，FG4-ECO-01 物品表接入库，见 DEBT-FG3LOG02-01）。
        /// 返还走 <see cref="HomeValleyConstruction.ReturnMaterials"/>：仓库有空间直接入库，放不下的变成地面物、生成搬运单等机器搬。
        /// </summary>
        public static GridOpResult TryRemoveBelts(CampaignState state, List<GridCell> cells)
        {
            if (state == null || cells == null || cells.Count == 0)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
            }
            if (!BeltNetworkService.IsRunning && !PipeNetworkService.IsRunning)
            {
                return GridOpResult.Fail(new GridReason(GridBlockReason.Busy, "logistics.reason.not_running"));
            }
            int removed = 0;
            int pipesRemoved = 0;
            long drained = 0;
            int refund = 0;
            int withItems = 0;
            int itemsReturned = 0;
            var returned = new List<ushort>();
            var planned = new List<GridCell>();
            var itemCounts = new SortedDictionary<ushort, int>();
            Vector2 at = Vector2.zero;
            HomeGridMap map = MapFor(state);
            foreach (GridCell c in cells)
            {
                if (HomeValleyConstruction.IsPlannedMarker(map.GetBelt(c)) || HomeValleyConstruction.IsPlannedMarker(map.GetPipe(c)))
                {
                    planned.Add(c);
                    continue;
                }
                // FG3-LOG-05：管线层的件——造价全额返还；储罐 / 阀门里的流体随之排空（储罐有存量时调用方已先确认）。
                if (PipeNetworkService.TryGetPiece(c, out PipePieceKind pk, out int ptier))
                {
                    if (PipeNetworkService.TryRemove(state, c, out long lost).Ok)
                    {
                        removed++;
                        pipesRemoved++;
                        drained += lost;
                        refund += PipePieceCost(pk, ptier);
                        at = new Vector2(c.X, c.Y);
                    }
                    continue;
                }
                if (!BeltNetworkService.IsRunning)
                {
                    continue;
                }
                // FG3-LOG-04：分流器 / 合流器按每座造价、地下传送带拆任一端就拆整条（两端返还）；同一条地下传送带的另一端再轮到时已经没了，跳过。
                if (!BeltNetworkService.TryGetPiece(c, out BeltNodeKind kind, out int tier))
                {
                    continue;
                }
                returned.Clear();
                if (BeltNetworkService.TryRemove(state, c, returned).Ok)
                {
                    removed++;
                    refund += PieceRefund(kind, tier);
                    at = new Vector2(c.X, c.Y);
                    if (returned.Count > 0)
                    {
                        withItems++;
                        foreach (ushort item in returned)
                        {
                            itemCounts[item] = itemCounts.TryGetValue(item, out int n) ? n + 1 : 1;
                            itemsReturned++;
                        }
                    }
                }
            }
            int cancelled = HomeValleyConstruction.CancelPlannedCells(state, planned);
            if (removed == 0 && cancelled == 0)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
            }
            string stamp = at.x.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "," + at.y.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                           + ":" + Core.GameClock.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (state.WorkOrders?.Length ?? 0);
            if (refund > 0)
            {
                HomeValleyConstruction.ReturnMaterials(state, at, CampaignEconomyLedger.ResourceScrap, refund, "belt-refund:" + stamp);
            }
            foreach (KeyValuePair<ushort, int> kv in itemCounts)
            {
                HomeValleyConstruction.ReturnMaterials(state, at, BeltItemResource(kv.Key), kv.Value, "belt-items:" + kv.Key + ":" + stamp);
            }
            LastBeltCount = removed;
            LastBeltScrap = refund;
            LastBeltsWithItems = withItems;
            LastBeltItemsReturned = itemsReturned;
            LastPlannedBeltsCancelled = cancelled;
            LastPipesRemoved = pipesRemoved;
            LastPipeDrainedMl = drained;
            return new GridOpResult(GridOpResult.Kind.BeltsRemoved, null);
        }

        /// <summary>传送带物品 → 家园资源类型。FG3-LOG-03：废料（logistics.item.scrap_id）回到仓库库存；其余物品在物品表（FG4-ECO-01）之前
        /// 按物品编号记（"item:&lt;编号&gt;"），落在地上不消失（DEBT-FG3LOG02-01）。</summary>
        public static string BeltItemResource(ushort item) => Logistics.BeltItems.ResourceOf(item);

        /// <summary>最近一次拆传送带时带着物品的格数 / 一并返还的物品件数 / 取消的规划格数。</summary>
        public static int LastBeltsWithItems { get; private set; }
        public static int LastBeltItemsReturned { get; private set; }
        public static int LastPlannedBeltsCancelled { get; private set; }
        /// <summary>FG3-LOG-05：最近一次拆除里有几格是管线件、随之排空了多少流体（毫升）。</summary>
        public static int LastPipesRemoved { get; private set; }
        public static long LastPipeDrainedMl { get; private set; }

        // ── 框选批量拆除（FGR-LOG-007；DEBT-FG0ARCH04-12）───────────────────────────────

        /// <summary>
        /// 规划框选拆除：框（闭区间，任意两角）里每座建筑按单座拆除的同一套规则分类（能标记 / 取消规划 / 不能拆 + 原因），
        /// 传送带格分成能拆（空）与带着物品的。建筑按记录逐座判断（O(建筑数)），传送带按框内的格子（框最大 grid.drag_max_cells 见方）。
        /// </summary>
        public static DemolishBoxPlan PlanDemolishBox(CampaignState state, GridCell a, GridCell b, DemolishBoxPlan into = null)
        {
            DemolishBoxPlan plan = into ?? new DemolishBoxPlan();
            plan.ToMark.Clear();
            plan.ToCancel.Clear();
            plan.Refused.Clear();
            plan.Belts.Clear();
            plan.CriticalNames.Clear();
            plan.AlreadyMarked = 0;
            plan.BeltsWithItems = 0;
            plan.Pipes = 0;
            plan.TankFluidMl = 0;
            plan.ResetPower(state);
            int max = GridContent.TuningInt("grid.drag_max_cells");
            var min = new GridCell(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
            var mx = new GridCell(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            mx = new GridCell(Math.Min(mx.X, min.X + max - 1), Math.Min(mx.Y, min.Y + max - 1));
            plan.Min = min;
            plan.Max = mx;
            if (state == null)
            {
                return plan;
            }
            HomeGridMap map = MapFor(state);
            foreach (BuildingRecord rec in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (rec == null || rec.RegionId != HomeValleyLayout.RegionId || !GridContent.TryGetBuilding(rec.BuildingTypeId, out BuildingGrid g))
                {
                    continue;
                }
                GridMath.FootprintBounds(new GridCell(rec.GridX, rec.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(rec.Rotation),
                    out GridCell bMin, out GridCell bMax);
                if (bMax.X < min.X || bMin.X > mx.X || bMax.Y < min.Y || bMin.Y > mx.Y)
                {
                    continue;
                }
                ClassifyForDemolish(state, rec, g, plan);
            }
            if (BeltNetworkService.IsRunning)
            {
                for (int y = min.Y; y <= mx.Y; y++)
                {
                    for (int x = min.X; x <= mx.X; x++)
                    {
                        var c = new GridCell(x, y);
                        ushort layer = map.GetBelt(c);
                        if (layer == 0)
                        {
                            continue;
                        }
                        if (HomeValleyConstruction.IsPlannedMarker(layer))
                        {
                            plan.Belts.Add(c); // FG3-LOG-02：规划中的传送带虚影——拆除 = 取消规划。
                            continue;
                        }
                        if (!BeltNetworkService.Kernel.TryGetCellInfo(x, y, out BeltCellInfo info))
                        {
                            continue;
                        }
                        if (info.Count > 0)
                        {
                            plan.BeltsWithItems++; // 带着物品的也拆（物品一并返还），这里只计数给 HUD 写明。
                        }
                        plan.Belts.Add(c);
                    }
                }
            }
            // FG3-LOG-05：框里的管线层（已建成的件与规划中的虚影）。储罐有存量时计入 TankFluidMl（拆除会排空，要先确认）。
            for (int y = min.Y; y <= mx.Y; y++)
            {
                for (int x = min.X; x <= mx.X; x++)
                {
                    var c = new GridCell(x, y);
                    if (map.GetPipe(c) == 0)
                    {
                        continue;
                    }
                    plan.Belts.Add(c);
                    plan.Pipes++;
                    if (PipeNetworkService.IsRunning && PipeNetworkService.Kernel.TryGetCellInfo(x, y, out PipeCellInfo pi) && pi.TankStockMl > 0)
                    {
                        plan.TankFluidMl += pi.TankStockMl;
                    }
                }
            }
            return plan;
        }

        private static void ClassifyForDemolish(CampaignState state, BuildingRecord b, BuildingGrid g, DemolishBoxPlan plan)
        {
            if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                plan.Refused.Add(new KeyValuePair<string, GridReason>(b.BuildingId, GridReason.Of(GridBlockReason.CannotDemolishCore)));
                return;
            }
            if (FindActiveDemolish(state, b.BuildingId) != null)
            {
                plan.AlreadyMarked++;
                return;
            }
            if (b.ConstructionState == BuildingConstructionState.Planned || b.ConstructionState == BuildingConstructionState.MaterialReserved
                || b.ConstructionState == BuildingConstructionState.Building)
            {
                if (FindActive(state, b.BuildingId, WorkOrderKind.Build) != null)
                {
                    plan.ToCancel.Add(b.BuildingId);
                }
                return;
            }
            GridReason? refuse = null;
            if (IsDemolishForbidden(b.BuildingTypeId))
            {
                refuse = GridReason.Of(GridBlockReason.NotRebuildable);
            }
            else if (FindRelocationGhost(state, b.BuildingId) != null)
            {
                refuse = GridReason.Of(GridBlockReason.Relocating);
            }
            else if (b.ConstructionState == BuildingConstructionState.Damaged)
            {
                refuse = GridReason.Of(GridBlockReason.DemolishDamaged);
            }
            else if (b.ConstructionState != BuildingConstructionState.Operational || HasActiveOrder(state, b.BuildingId, WorkOrderKind.Repair))
            {
                refuse = GridReason.Of(GridBlockReason.Busy);
            }
            if (refuse != null)
            {
                plan.Refused.Add(new KeyValuePair<string, GridReason>(b.BuildingId, refuse.Value));
                return;
            }
            plan.ToMark.Add(b.BuildingId);
            if (g.Critical == 1)
            {
                plan.CriticalNames.Add(DisplayName(b.BuildingTypeId));
            }
        }

        /// <summary>执行框选拆除规划（调用方已按 <see cref="DemolishBoxPlan.NeedsConfirm"/> 确认过）：逐座标记 / 取消规划，拆掉空传送带。
        /// 每座都重新走单座拆除的入口（状态可能在确认框开着时变了，按执行那一刻的状态判断）。</summary>
        public static GridOpResult ExecuteDemolishBox(CampaignState state, DemolishBoxPlan plan)
        {
            if (state == null || plan == null || plan.IsEmpty)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
            }
            int marked = 0;
            int cancelled = 0;
            foreach (string id in plan.ToMark)
            {
                if (FindActiveDemolish(state, id) != null)
                {
                    continue; // 确认框开着的时候已经被标记了：批量拆除不把它反过来“取消标记”。
                }
                if (TryToggleDemolish(state, id).Outcome == GridOpResult.Kind.DemolishMarked)
                {
                    marked++;
                }
            }
            foreach (string id in plan.ToCancel)
            {
                if (TryToggleDemolish(state, id).Outcome == GridOpResult.Kind.PlanCancelled)
                {
                    cancelled++;
                }
            }
            int belts = 0;
            if (plan.Belts.Count > 0 && TryRemoveBelts(state, plan.Belts).Outcome == GridOpResult.Kind.BeltsRemoved)
            {
                belts = LastBeltCount + LastPlannedBeltsCancelled; // FG3-LOG-02：取消的传送带虚影格也算这次框选处理掉的传送带
            }
            LastBatchMarked = marked;
            LastBatchCancelled = cancelled;
            LastBatchBelts = belts;
            if (marked + cancelled + belts == 0)
            {
                return GridOpResult.Fail(plan.Refused.Count > 0 ? plan.Refused[0].Value : GridReason.Of(GridBlockReason.NoBuilding));
            }
            Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstBatchDemolish);
            return new GridOpResult(GridOpResult.Kind.BatchDemolished, null);
        }

        public static int LastBatchMarked { get; private set; }
        public static int LastBatchCancelled { get; private set; }
        public static int LastBatchBelts { get; private set; }
    }
}
