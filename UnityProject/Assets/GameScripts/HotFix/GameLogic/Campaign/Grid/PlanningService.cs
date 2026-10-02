using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using BuildTool = GameConfig.fg.BuildTool;

namespace GameLogic.Campaign.Grid
{
    /// <summary>FG3-LOG-07：粘贴 / 重放预览里的一件（世界格、朝向、能不能放与原因）。</summary>
    public sealed class PasteItem
    {
        public int Index;
        public PlanEntryKind Kind;
        public string Id;
        /// <summary>建筑的枢轴格 / 物流件的格子 / 地下传送带入口。</summary>
        public GridCell Cell;
        /// <summary>地下传送带出口。</summary>
        public GridCell Cell2;
        /// <summary>建筑：朝向（度）；物流件：方向（0 北 1 东 2 南 3 西）。</summary>
        public int Rot;
        public int S0;
        public int S1;
        public int S2;
        public bool Ok;
        public GridReason? Reason;
        /// <summary>画预览用的格子（建筑是占地，地下传送带是两端，其余一格）。</summary>
        public readonly List<GridCell> Cells = new List<GridCell>(9);
    }

    /// <summary>FG3-LOG-07（FGR-LOG-005）：一次粘贴的规划——布局、落点、旋转、逐件校验结果与成本。只在光标换格 / 转向时重算。</summary>
    public sealed class PastePlan
    {
        public PlanEntryBlock Source;
        public GridCell Anchor;
        public int Quarter;
        public bool Absolute;
        public readonly List<PasteItem> Items = new List<PasteItem>(64);
        public int OkCount;
        public int BadCount;
        public int LockedCount;
        public int UnknownCount;
        public int Cost;
        /// <summary>FG4-ECO-11：能放下的建筑在废料之外要的材料合计（预览与完成提示写出来，缺货写从哪里获得）。</summary>
        public readonly Economy.BuildMaterialTally Extras = new Economy.BuildMaterialTally();
        public int Stock;
        public int FirstBad = -1;
        public int Width;
        public int Height;
        // 按布局缓存的种类 / 工具 / 边界（换格、转向时不重算）。
        internal PlanEntryBlock CachedFor;
        internal int CachedRevision;
        internal BuildTool[] Tools = new BuildTool[0];
        internal PlanEntryKind[] Kinds = new PlanEntryKind[0];
        internal int MinX;
        internal int MinY;
        internal int MaxX;
        internal int MaxY;

        public PasteItem FirstBadItem => FirstBad >= 0 && FirstBad < Items.Count ? Items[FirstBad] : null;

        /// <summary>第一处放不了的件：“第 k 件 名字（x, y）：原因”（当前语言；全部能放时为空串）。</summary>
        public string DescribeFirstBad()
        {
            PasteItem bad = FirstBadItem;
            if (bad == null || bad.Reason == null)
            {
                return string.Empty;
            }
            return GameText.Format("plan.paste.bad_item", bad.Index + 1, PlanEntries.NameOf(bad.Id), bad.Cell.X, bad.Cell.Y, bad.Reason.Value.Describe());
        }
    }

    /// <summary>FG3-LOG-07：一次放置（粘贴 / 重做 / 撤销拆除）的结果。</summary>
    public sealed class PlanApplyResult
    {
        public int PlacedBuildings;
        public int PlacedPieces;
        public int Failed;
        public GridReason? FirstFailure;
        public string FirstFailureName;
        /// <summary>放下的建筑虚影（按放置顺序）：新 ID 与它的条目下标。</summary>
        public readonly List<string> BuildingIds = new List<string>();
        public readonly List<int> BuildingIndex = new List<int>();
        /// <summary>放下的物流件（绝对格，撤销时按它取消 / 拆除）。</summary>
        public readonly PlanEntries.Builder Pieces = new PlanEntries.Builder();
        public readonly List<string> PlanIds = new List<string>();

        public int Placed => PlacedBuildings + PlacedPieces;
    }

    /// <summary>
    /// FG3-LOG-07（FG03 FGR-LOG-005 批量操作；FGR-LOG-009 撤销重做的放置 / 取消；FGR-LOG-011 吸管）：规划工具的服务端。
    /// - 框选复制（<see cref="Capture"/>）：框里的建筑（建成的与虚影）、传送带 / 分流器 / 合流器 / 地下传送带、管线 / 泵 / 储罐 / 阀门连同设置，存成相对坐标的布局。
    /// - 粘贴（<see cref="PlanPaste"/> → <see cref="Apply"/>）：可以整体旋转 90°；逐件按正式放置规则校验（与手动放置同一套：迷雾、地形、占用、解锁、跨度、流体……），
    ///   合法的部分放下虚影（设置随虚影，建成时生效），不合法的部分不放、在预览里标红叉并写明原因（负向“粘贴到非法位置的部分给出明确标记”）。
    /// - 撤销 / 重做要的“拆掉这些件”“放回这些件”（<see cref="RemovePieces"/> / <see cref="Apply"/> 绝对坐标）。
    /// 只改战役状态（走 HomeGridService / HomeValleyConstruction 的正式入口），不依赖镜头（FGR-BASE-021）；开销 O(件数)，只在玩家操作时发生。
    /// </summary>
    public static class PlanningService
    {
        public static int MaxEntries => Math.Max(1, GridContent.TuningInt("plan.layout_max_entries"));

        private static readonly GridPlacementResult Scratch = new GridPlacementResult();
        private static readonly HashSet<long> SeenTunnels = new HashSet<long>();
        private static readonly List<int> FluidScratch = new List<int>(4);

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        // ── 框选复制 ──────────────────────────────────────────────────────────────

        /// <summary>最近一次复制时略过的件数（本局不能建造的开局建筑、只有一端在框里的地下传送带）。</summary>
        public static int LastSkipped { get; private set; }

        /// <summary>
        /// 把框（闭区间，任意两角；每边最多 grid.drag_max_cells 格）里的东西存成布局（坐标相对框的左下角）。
        /// 建筑：占地和框有交集就算（与框选拆除同一口径）；核心、搬迁 / 升级中的目标虚影、本局不能建造的开局建筑不复制（计入 <see cref="LastSkipped"/>）。
        /// 物流件：建成的与还没建成的虚影都复制；地下传送带整条（入口在框里才算）。空框 / 超过上限返回 null 并给原因。
        /// </summary>
        public static PlanEntryBlock Capture(CampaignState state, GridCell a, GridCell b, out GridReason? refuse)
        {
            refuse = null;
            LastSkipped = 0;
            if (state == null)
            {
                refuse = GridReason.Of(GridBlockReason.NoRegion);
                return null;
            }
            int max = GridContent.TuningInt("grid.drag_max_cells");
            var min = new GridCell(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
            var mx = new GridCell(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            mx = new GridCell(Math.Min(mx.X, min.X + max - 1), Math.Min(mx.Y, min.Y + max - 1));
            var builder = new PlanEntries.Builder();
            HomeGridMap map = HomeGridService.MapFor(state);
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
                if (rec.BuildingTypeId == HomeValleyLayout.BuildingTypeCore || HomeGridService.IsRelocationGhost(rec) || g.Placeable != 1)
                {
                    LastSkipped++;
                    continue;
                }
                PlanSettings.BuildingSettings(state, rec, out int cs0, out int cs1, out int cs2); // FG4-ECO-05：复制带上配方 / 刻录目标。
                builder.Add(rec.BuildingTypeId, rec.GridX - min.X, rec.GridY - min.Y, GridMath.NormalizeRotation(rec.Rotation), s0: cs0, s1: cs1, s2: cs2);
            }
            SeenTunnels.Clear();
            for (int y = min.Y; y <= mx.Y; y++)
            {
                for (int x = min.X; x <= mx.X; x++)
                {
                    var c = new GridCell(x, y);
                    if (map.GetBelt(c) != 0)
                    {
                        AddPiece(state, c, false, builder, min, box: true, bmin: min, bmax: mx);
                    }
                    if (map.GetPipe(c) != 0)
                    {
                        AddPiece(state, c, true, builder, min, box: true, bmin: min, bmax: mx);
                    }
                }
            }
            if (builder.Count == 0)
            {
                refuse = new GridReason(GridBlockReason.NoBuilding, "plan.reason.copy_empty");
                return null;
            }
            if (builder.Count > MaxEntries)
            {
                refuse = new GridReason(GridBlockReason.TooManyEntries, "plan.reason.too_many", builder.Count.ToString(), MaxEntries.ToString());
                return null;
            }
            return builder.Build();
        }

        /// <summary>
        /// 这些格子上的物流件（建成的与虚影）的快照（绝对坐标；拆除前记下，撤销时放回）。地下传送带按整条记一次（给哪一端都行）。
        /// </summary>
        public static PlanEntryBlock SnapshotCells(CampaignState state, IReadOnlyList<GridCell> cells)
        {
            var builder = new PlanEntries.Builder();
            if (state == null || cells == null)
            {
                return builder.Build();
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            SeenTunnels.Clear();
            var origin = new GridCell(0, 0);
            foreach (GridCell c in cells)
            {
                if (map.GetBelt(c) != 0)
                {
                    AddPiece(state, c, false, builder, origin, box: false, bmin: default, bmax: default);
                }
                if (map.GetPipe(c) != 0)
                {
                    AddPiece(state, c, true, builder, origin, box: false, bmin: default, bmax: default);
                }
            }
            return builder.Build();
        }

        /// <summary>一格上的物流件 → 一条条目（坐标减去 <paramref name="origin"/>）。box = true 时地下传送带入口必须在框里。</summary>
        private static void AddPiece(CampaignState state, GridCell c, bool pipeLayer, PlanEntries.Builder builder, GridCell origin, bool box, GridCell bmin, GridCell bmax)
        {
            HomeGridMap map = HomeGridService.MapFor(state);
            ushort layer = pipeLayer ? map.GetPipe(c) : map.GetBelt(c);
            if (HomeValleyConstruction.IsPlannedMarker(layer))
            {
                if (!HomeValleyConstruction.TryFindPlannedCell(state, c, out PlannedBeltRecord p, out int i) || (p.PipePiece > 0) != pipeLayer)
                {
                    return;
                }
                if (pipeLayer)
                {
                    var pk = (PipePieceKind)Math.Max(0, Math.Min((int)PipePieceKind.Underground, p.PipePiece - 1));
                    string pid = PlanEntries.ToolIdForPipe(pk, p.Tier);
                    if (pid != null)
                    {
                        builder.Add(pid, c.X - origin.X, c.Y - origin.Y, p.Dirs[i], s0: p.PipeSettings);
                    }
                    return;
                }
                var nk = (BeltNodeKind)p.NodeKind;
                string id = PlanEntries.ToolIdForBelt(nk, p.Tier);
                if (id == null)
                {
                    return;
                }
                if (nk == BeltNodeKind.UndergroundIn)
                {
                    if (p.Xs.Length < 2 || !SeenTunnels.Add(Key(p.Xs[0], p.Ys[0])))
                    {
                        return;
                    }
                    if (box && !Inside(new GridCell(p.Xs[0], p.Ys[0]), bmin, bmax))
                    {
                        LastSkipped++;
                        return;
                    }
                    builder.Add(id, p.Xs[0] - origin.X, p.Ys[0] - origin.Y, p.Dirs[0], p.Xs[1] - origin.X, p.Ys[1] - origin.Y);
                    return;
                }
                int s0 = 0, s1 = 0, s2 = 0;
                if (nk == BeltNodeKind.Splitter)
                {
                    s0 = PlanSettings.PackSplitter(Math.Max(1, p.RatioL), Math.Max(1, p.RatioR), p.PriorityOut);
                    s1 = p.FilterL;
                    s2 = p.FilterR;
                }
                else if (nk == BeltNodeKind.Merger)
                {
                    s0 = p.PriorityIn;
                }
                builder.Add(id, c.X - origin.X, c.Y - origin.Y, p.Dirs[i], s0: s0, s1: s1, s2: s2);
                return;
            }
            if (pipeLayer)
            {
                if (!PipeNetworkService.IsRunning || !PipeNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out PipeCellInfo pi))
                {
                    return;
                }
                string pid = PlanEntries.ToolIdForPipe(pi.Kind, pi.Tier);
                if (pid == null)
                {
                    return;
                }
                int ps = pi.Kind == PipePieceKind.Tank ? PlanSettings.PackTank(pi.TankMode, pi.Priority) : pi.Kind == PipePieceKind.Valve ? PlanSettings.PackValve(pi.ValveOpen) : 0;
                builder.Add(pid, c.X - origin.X, c.Y - origin.Y, pi.Dir, s0: ps);
                return;
            }
            if (!BeltNetworkService.IsRunning || !BeltNetworkService.TryGetPiece(c, out BeltNodeKind kind, out int tier))
            {
                return;
            }
            string tid = PlanEntries.ToolIdForBelt(kind, tier);
            if (tid == null)
            {
                return;
            }
            if (kind == BeltNodeKind.UndergroundIn || kind == BeltNodeKind.UndergroundOut)
            {
                if (!BeltNetworkService.TryGetNode(c, out BeltNodeInfo u) || !SeenTunnels.Add(Key(u.EntranceX, u.EntranceY)))
                {
                    return;
                }
                if (box && !Inside(new GridCell(u.EntranceX, u.EntranceY), bmin, bmax))
                {
                    LastSkipped++;
                    return;
                }
                builder.Add(tid, u.EntranceX - origin.X, u.EntranceY - origin.Y, (int)u.Dir, u.ExitX - origin.X, u.ExitY - origin.Y);
                return;
            }
            BeltNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out BeltCellInfo info);
            int n0 = 0, n1 = 0, n2 = 0;
            if ((kind == BeltNodeKind.Splitter || kind == BeltNodeKind.Merger) && BeltNetworkService.TryGetNode(c, out BeltNodeInfo node))
            {
                if (kind == BeltNodeKind.Splitter)
                {
                    n0 = PlanSettings.PackSplitter(node.RatioL, node.RatioR, (int)node.PriorityOut);
                    n1 = node.FilterL;
                    n2 = node.FilterR;
                }
                else
                {
                    n0 = (int)node.PriorityIn;
                }
            }
            builder.Add(tid, c.X - origin.X, c.Y - origin.Y, (int)info.Dir, s0: n0, s1: n1, s2: n2);
        }

        private static bool Inside(GridCell c, GridCell min, GridCell max) => c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y;

        // ── 粘贴规划 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 规划一次粘贴：布局的中心格落在 <paramref name="anchor"/>，整体顺时针转 <paramref name="quarter"/> 个 90°（<paramref name="absolute"/> = true 时
        /// 条目坐标就是世界格，不平移不旋转——撤销 / 重做放回原处用）。逐件按正式规则校验：建筑 = <see cref="HomeGridService.ValidatePlacement"/>（含可放置、解锁、上限）；
        /// 传送带类 = <see cref="HomeGridService.ValidateBeltCell"/> + 工具解锁 + 地下跨度 / 重叠；管线类 = <see cref="HomeGridService.ValidatePipeCell"/> + 流体规则
        /// （这次粘贴里相连的一组管线件与四周已有网络、泵的来源最多一种流体；阀门两侧流体相同或有一侧没有）。O(件数)。
        /// </summary>
        public static PastePlan PlanPaste(CampaignState state, PlanEntryBlock block, GridCell anchor, int quarter, bool absolute = false, PastePlan into = null)
        {
            PastePlan plan = into ?? new PastePlan();
            plan.Source = block;
            plan.Anchor = anchor;
            plan.Quarter = quarter & 3;
            plan.Absolute = absolute;
            plan.OkCount = 0;
            plan.Extras.Clear();
            plan.BadCount = 0;
            plan.LockedCount = 0;
            plan.UnknownCount = 0;
            plan.Cost = 0;
            plan.FirstBad = -1;
            plan.Stock = state != null ? state.Scrap : 0;
            int n = PlanEntries.CountOf(block);
            while (plan.Items.Count < n)
            {
                plan.Items.Add(new PasteItem());
            }
            if (plan.Items.Count > n)
            {
                plan.Items.RemoveRange(n, plan.Items.Count - n);
            }
            // 同一个布局换格 / 转向时不重算种类与边界（只在布局或内容表变化时算一次，O(件数)）。
            if (!ReferenceEquals(plan.CachedFor, block) || plan.CachedRevision != GridContent.Revision || plan.Tools.Length < n)
            {
                PlanEntries.Bounds(block, out plan.MinX, out plan.MinY, out plan.MaxX, out plan.MaxY);
                if (plan.Tools.Length < n)
                {
                    plan.Tools = new BuildTool[n];
                    plan.Kinds = new PlanEntryKind[n];
                }
                for (int i = 0; i < n; i++)
                {
                    plan.Kinds[i] = PlanEntries.KindOf(block.Ids[i], out plan.Tools[i]);
                }
                plan.CachedFor = block;
                plan.CachedRevision = GridContent.Revision;
            }
            int minX = plan.MinX, minY = plan.MinY, maxX = plan.MaxX, maxY = plan.MaxY;
            plan.Width = maxX - minX + 1;
            plan.Height = maxY - minY + 1;
            int cx = GridMath.FloorDiv(minX + maxX, 2);
            int cy = GridMath.FloorDiv(minY + maxY, 2);
            int q = plan.Quarter;
            HomeGridService.BeginCellBatch(state);
            try
            {
            for (int i = 0; i < n; i++)
            {
                PasteItem it = plan.Items[i];
                it.Index = i;
                it.Id = block.Ids[i];
                it.Kind = plan.Kinds[i];
                BuildTool tool = plan.Tools[i];
                it.S0 = block.S0[i];
                it.S1 = block.S1[i];
                it.S2 = block.S2[i];
                it.Cells.Clear();
                it.Reason = null;
                if (absolute)
                {
                    it.Cell = new GridCell(block.Xs[i], block.Ys[i]);
                    it.Cell2 = new GridCell(block.X2s[i], block.Y2s[i]);
                    it.Rot = block.Rots[i];
                }
                else
                {
                    it.Cell = PlanEntries.Place(block.Xs[i] - cx, block.Ys[i] - cy, anchor, q);
                    it.Cell2 = PlanEntries.Place(block.X2s[i] - cx, block.Y2s[i] - cy, anchor, q);
                    it.Rot = it.Kind == PlanEntryKind.Building ? GridMath.NormalizeRotation(block.Rots[i] + 90 * q) : (block.Rots[i] + q) & 3;
                }
                if (state == null)
                {
                    it.Reason = GridReason.Of(GridBlockReason.NoRegion);
                }
                else
                {
                    ValidateItem(state, it, tool);
                }
                if (it.Cells.Count == 0)
                {
                    it.Cells.Add(it.Cell);
                }
            }
            if (state != null)
            {
                CheckPasteFluids(state, plan);
            }
            }
            finally
            {
                HomeGridService.EndCellBatch();
            }
            for (int i = 0; i < n; i++)
            {
                PasteItem it = plan.Items[i];
                it.Ok = it.Reason == null;
                if (it.Ok)
                {
                    plan.OkCount++;
                    plan.Cost += PlanEntries.CostOf(it.Id);
                    plan.Extras.Add(Economy.BuildMaterials.NewBuild(it.Id)); // FG4-ECO-11：粘贴造价里的额外材料（物流件没有，返回空）
                }
                else
                {
                    plan.BadCount++;
                    if (plan.FirstBad < 0)
                    {
                        plan.FirstBad = i;
                    }
                    GridBlockReason code = it.Reason.Value.Code;
                    if (code == GridBlockReason.Locked || code == GridBlockReason.ToolLocked)
                    {
                        plan.LockedCount++;
                    }
                    else if (code == GridBlockReason.UnknownType)
                    {
                        plan.UnknownCount++;
                    }
                }
            }
            return plan;
        }

        private static void ValidateItem(CampaignState state, PasteItem it, BuildTool tool)
        {
            switch (it.Kind)
            {
                case PlanEntryKind.Unknown:
                    it.Reason = new GridReason(GridBlockReason.UnknownType, "plan.reason.unknown_entry", it.Id ?? string.Empty);
                    return;
                case PlanEntryKind.Building:
                {
                    GridPlacementResult r = HomeGridService.ValidatePlacement(state, it.Id, it.Cell, it.Rot, checkCost: false, into: Scratch);
                    it.Cells.AddRange(r.Cells);
                    if (!r.Ok)
                    {
                        it.Reason = r.Reasons[0];
                    }
                    return;
                }
            }
            if (!BuildCatalog.IsUnlocked(state, tool.UnlockRule))
            {
                it.Reason = new GridReason(GridBlockReason.ToolLocked, "grid.reason.not_unlocked_tool", tool.UnlockHintKey);
                it.Cells.Add(it.Cell);
                return;
            }
            if (PlanEntries.IsPipeLayer(it.Kind))
            {
                it.Cells.Add(it.Cell);
                GridPlacementResult r = HomeGridService.ValidatePipeCell(state, it.Cell, PlanEntries.PipeKindOf(it.Kind), Scratch);
                if (!r.Ok)
                {
                    it.Reason = r.Reasons[0];
                }
                return;
            }
            it.Cells.Add(it.Cell);
            GridPlacementResult c1 = HomeGridService.ValidateBeltCell(state, it.Cell, Scratch);
            if (!c1.Ok)
            {
                it.Reason = c1.Reasons[0];
                return;
            }
            if (it.Kind != PlanEntryKind.Underground)
            {
                return;
            }
            it.Cells.Add(it.Cell2);
            GridPlacementResult c2 = HomeGridService.ValidateBeltCell(state, it.Cell2, Scratch);
            if (!c2.Ok)
            {
                it.Reason = c2.Reasons[0];
                return;
            }
            int distance = Math.Abs(it.Cell2.X - it.Cell.X) + Math.Abs(it.Cell2.Y - it.Cell.Y);
            int span = BeltNetworkService.UndergroundSpan(tool.Tier);
            if ((it.Cell2.X != it.Cell.X && it.Cell2.Y != it.Cell.Y) || distance < 1)
            {
                it.Reason = new GridReason(GridBlockReason.UndergroundShape, "grid.reason.under_not_straight");
            }
            else if (distance - 1 > span)
            {
                it.Reason = new GridReason(GridBlockReason.UndergroundSpan, "grid.reason.under_too_far", tool.NameKey, span.ToString(), (distance - 1).ToString());
            }
            else if (HomeGridService.TryFindUndergroundOverlap(state, it.Cell, (BeltDir)it.Rot, distance, out GridCell at))
            {
                it.Reason = new GridReason(GridBlockReason.UndergroundOccupied, "grid.reason.under_occupied", at.X.ToString(), at.Y.ToString());
            }
        }

        private const int TunnelNone = 0, TunnelPaste = 1, TunnelBlocked = 2, TunnelExisting = 3;

        /// <summary>粘贴件的等级（工具表里的等级，夹到管线等级范围）。</summary>
        private static int PasteTier(PasteItem it)
        {
            PlanEntries.KindOf(it.Id, out BuildTool tool);
            return Math.Max(0, Math.Min(PipeConst.TierCount - 1, tool?.Tier ?? 0));
        }

        /// <summary>
        /// 粘贴的地下管线口 <paramref name="k"/> 沿朝向、在自己的跨度以内先碰到哪一口：这次粘贴里朝回来且对方也够得着 = 配对（<see cref="TunnelPaste"/>，<paramref name="mate"/>）；
        /// 这次粘贴里的其它口 = 被挡住（<see cref="TunnelBlocked"/>）；已有的地下口 = 交给内核按已有网络查（<see cref="TunnelExisting"/>）；都没有 = <see cref="TunnelNone"/>。O(跨度)。
        /// </summary>
        private static int PasteTunnel(PastePlan plan, Dictionary<long, int> byCell, int k, out int mate)
        {
            mate = -1;
            PasteItem it = plan.Items[k];
            int dir = it.Rot & 3;
            int span = PipeNetworkService.UndergroundSpan(PasteTier(it));
            int dx = BeltDirs.Dx(dir), dy = BeltDirs.Dy(dir);
            for (int step = 1; step <= span + 1; step++)
            {
                int x = it.Cell.X + dx * step, y = it.Cell.Y + dy * step;
                if (byCell.TryGetValue(Key(x, y), out int nb) && plan.Items[nb].Kind == PlanEntryKind.PipeUnderground)
                {
                    PasteItem other = plan.Items[nb];
                    if ((other.Rot & 3) == BeltDirs.Opposite(dir) && step <= PipeNetworkService.UndergroundSpan(PasteTier(other)) + 1)
                    {
                        mate = nb;
                        return TunnelPaste;
                    }
                    return TunnelBlocked;
                }
                if (PipeNetworkService.TryGetPiece(new GridCell(x, y), out PipePieceKind pk, out _) && pk == PipePieceKind.Underground)
                {
                    return TunnelExisting;
                }
            }
            return TunnelNone;
        }

        /// <summary>
        /// 流体规则（FGR-LOG-040）：这次粘贴里互相挨着的管线 / 泵 / 储罐连成一组，一组加上四周已有网络与泵的来源最多一种流体，否则这一组全部标“会接错流体”；
        /// 阀门单独查（两侧流体相同或有一侧没有、不能直接接另一个阀门）。
        /// </summary>
        private static void CheckPasteFluids(CampaignState state, PastePlan plan)
        {
            if (!PipeNetworkService.IsRunning)
            {
                return;
            }
            var byCell = new Dictionary<long, int>();
            for (int i = 0; i < plan.Items.Count; i++)
            {
                PasteItem it = plan.Items[i];
                if (it.Reason == null && PlanEntries.IsPipeLayer(it.Kind) && it.Kind != PlanEntryKind.Valve)
                {
                    byCell[Key(it.Cell.X, it.Cell.Y)] = i;
                }
            }
            var visited = new HashSet<int>();
            var group = new List<int>(16);
            var queue = new Queue<int>();
            foreach (KeyValuePair<long, int> start in byCell)
            {
                if (!visited.Add(start.Value))
                {
                    continue;
                }
                group.Clear();
                queue.Enqueue(start.Value);
                while (queue.Count > 0)
                {
                    int k = queue.Dequeue();
                    group.Add(k);
                    GridCell c = plan.Items[k].Cell;
                    for (int d = 0; d < 4; d++)
                    {
                        if (byCell.TryGetValue(Key(c.X + BeltDirs.Dx(d), c.Y + BeltDirs.Dy(d)), out int nb) && visited.Add(nb))
                        {
                            queue.Enqueue(nb);
                        }
                    }
                    // FG4-ECO-04 修复轮（P2）：这次粘贴里配成一对的两个地下管线口属于同一组（地下段把两头接成一个网络）。
                    if (plan.Items[k].Kind == PlanEntryKind.PipeUnderground && PasteTunnel(plan, byCell, k, out int mate) == TunnelPaste && visited.Add(mate))
                    {
                        queue.Enqueue(mate);
                    }
                }
                FluidScratch.Clear();
                foreach (int k in group)
                {
                    PasteItem it = plan.Items[k];
                    if (it.Kind == PlanEntryKind.Pump)
                    {
                        int src = PipeNetworkService.SourceFluidAt(state, it.Cell);
                        if (src > 0 && !FluidScratch.Contains(src))
                        {
                            FluidScratch.Add(src);
                        }
                    }
                    // FG4-ECO-04：地下管线口按它的朝向（地面一侧 + 地下配对口）查流体、按它自己的等级算跨度；地下那一侧先碰到的是这次粘贴里的口时
                    // （配对 = 已并进同一组；同向 = 被挡住），不再按已有网络查地下那一侧（修复轮 P2：之前一对粘贴口会分成两组、T2 按 T1 跨度找）。
                    bool under = it.Kind == PlanEntryKind.PipeUnderground;
                    bool tunnel = !under || PasteTunnel(plan, byCell, k, out _) == TunnelExisting;
                    PipeNetworkService.AddAdjacentFluids(it.Cell, PlanEntries.PipeKindOf(it.Kind), under ? it.Rot : 0, FluidScratch, under ? PasteTier(it) : 0, tunnel);
                }
                if (FluidScratch.Count > 1)
                {
                    var reason = new GridReason(GridBlockReason.PipeFluidConflict, "grid.reason.pipe_fluid_conflict",
                        PipeNetworkService.FluidName(FluidScratch[0]), PipeNetworkService.FluidName(FluidScratch[1]));
                    foreach (int k in group)
                    {
                        plan.Items[k].Reason = reason;
                    }
                }
            }
            foreach (PasteItem it in plan.Items)
            {
                if (it.Reason != null || it.Kind != PlanEntryKind.Valve)
                {
                    continue;
                }
                PipeResult vr = PipeNetworkService.CheckValve(it.Cell, it.Rot, out int va, out int vb);
                if (vr == PipeResult.FluidConflict)
                {
                    it.Reason = new GridReason(GridBlockReason.PipeFluidConflict, "logistics.pipe.reason.valve_conflict", PipeNetworkService.FluidName(va), PipeNetworkService.FluidName(vb));
                }
                else if (vr == PipeResult.ValveChained)
                {
                    it.Reason = GridReason.Of(GridBlockReason.PipeValveChained);
                }
            }
        }

        // ── 放下（粘贴 / 撤销拆除 / 重做放置）─────────────────────────────────────────

        /// <summary>
        /// 按规划放下全部能放的件（非法的不放，计入 <see cref="PlanApplyResult.Failed"/>）：建筑走 <see cref="HomeGridService.TryPlace"/>（每座一张施工单）并写上电力优先级；
        /// 物流件按种类与等级分组，每组一份规划（<see cref="HomeValleyConstruction.PlanBelts"/>：一张施工单，机器按顺序一格一格建），节点 / 储罐 / 阀门的设置写进规划、建成时生效。
        /// 放之前每件再按当下的状态校验一次（前面放下的件可能占了后面的格子）。不扣材料（虚影等材料，FGR-LOG-006）。
        /// </summary>
        public static PlanApplyResult Apply(CampaignState state, PastePlan plan)
        {
            var result = new PlanApplyResult();
            if (state == null || plan == null)
            {
                return result;
            }
            // 1. 建筑（先放建筑：它们占格，物流件的复核会看到）。
            foreach (PasteItem it in plan.Items)
            {
                if (!it.Ok || it.Kind != PlanEntryKind.Building)
                {
                    continue;
                }
                GridOpResult r = HomeGridService.TryPlace(state, it.Id, it.Cell, it.Rot);
                if (!r.Success)
                {
                    Fail(result, it, r.Placement != null && r.Placement.Reasons.Count > 0 ? r.Placement.Reasons[0] : r.Reason ?? GridReason.Of(GridBlockReason.Busy));
                    continue;
                }
                BuildingRecord b = HomeGridService.FindBuilding(state, r.BuildingId);
                if (b != null)
                {
                    // 虚影还没进电网：优先级直接写记录，建成接入时按它仲裁；FG4-ECO-05：配方 / 刻录目标写进这座虚影的生产记录（建成后按它工作）。
                    PlanSettings.ApplyBuildingSettings(state, b, it.S0, it.S1, it.S2, out _);
                }
                result.PlacedBuildings++;
                result.BuildingIds.Add(r.BuildingId);
                result.BuildingIndex.Add(it.Index);
            }
            // 2. 物流件：复核后按（种类, 等级）分组。
            bool beltsRunning = BeltNetworkService.IsRunning;
            bool pipesOk = PipeNetworkService.IsRunning && !PipeNetworkService.SavedDataPreserved;
            var groups = new Dictionary<int, List<PasteItem>>();
            foreach (PasteItem it in plan.Items)
            {
                if (!it.Ok || it.Kind == PlanEntryKind.Building || it.Kind == PlanEntryKind.Unknown)
                {
                    continue;
                }
                bool pipe = PlanEntries.IsPipeLayer(it.Kind);
                if (pipe ? !pipesOk : !beltsRunning)
                {
                    Fail(result, it, new GridReason(GridBlockReason.Busy, pipe ? "logistics.pipe.reason.not_running" : "logistics.reason.not_running"));
                    continue;
                }
                GridPlacementResult again = pipe ? HomeGridService.ValidatePipeCell(state, it.Cell, PlanEntries.PipeKindOf(it.Kind), Scratch)
                    : HomeGridService.ValidateBeltCell(state, it.Cell, Scratch);
                if (!again.Ok)
                {
                    Fail(result, it, again.Reasons[0]);
                    continue;
                }
                if (it.Kind == PlanEntryKind.Underground)
                {
                    GridPlacementResult exit = HomeGridService.ValidateBeltCell(state, it.Cell2, Scratch);
                    if (!exit.Ok)
                    {
                        Fail(result, it, exit.Reasons[0]);
                        continue;
                    }
                }
                // 单格一份的：节点、地下传送带、泵（泵的流体来源按格认定）、储罐、阀门（各自带设置）；传送带 / 管线按等级合并成一份。
                bool single = it.Kind != PlanEntryKind.Belt && it.Kind != PlanEntryKind.Pipe;
                PlanEntries.KindOf(it.Id, out BuildTool tool);
                int key = single ? -1 - it.Index : ((int)it.Kind << 8) | (tool?.Tier ?? 0);
                if (!groups.TryGetValue(key, out List<PasteItem> list))
                {
                    groups[key] = list = new List<PasteItem>(8);
                }
                list.Add(it);
            }
            var keys = new List<int>(groups.Keys);
            keys.Sort((x, y) => y.CompareTo(x)); // 合并组（正键）先，单件（负键）按条目顺序
            foreach (int key in keys)
            {
                List<PasteItem> list = groups[key];
                PasteItem first = list[0];
                PlanEntries.KindOf(first.Id, out BuildTool tool);
                bool pipe = PlanEntries.IsPipeLayer(first.Kind);
                var path = new BeltPathPlan
                {
                    ToolId = first.Id,
                    // 修复轮：地下管线口也按工具等级粘贴（之前 T2 地下口会被粘成 T1，跨度变短、造价却按 T2 扣）。
                    Tier = pipe ? (first.Kind == PlanEntryKind.Pipe || first.Kind == PlanEntryKind.PipeUnderground ? Math.Max(0, Math.Min(PipeConst.TierCount - 1, tool.Tier)) : 0) : tool.Tier,
                    ScrapPerCell = tool.ScrapPerCell,
                    IsPipe = pipe,
                    Pipe = PlanEntries.PipeKindOf(first.Kind),
                    Kind = pipe ? BeltNodeKind.Belt : PlanEntries.NodeKindOf(first.Kind),
                    PipeFluid = first.Kind == PlanEntryKind.Pump ? PipeNetworkService.SourceFluidAt(state, first.Cell) : 0,
                };
                foreach (PasteItem it in list)
                {
                    path.Cells.Add(it.Cell);
                    path.Dirs.Add((BeltDir)(it.Rot & 3));
                    if (it.Kind == PlanEntryKind.Underground)
                    {
                        path.Cells.Add(it.Cell2);
                        path.Dirs.Add((BeltDir)(it.Rot & 3));
                    }
                }
                string planId = HomeValleyConstruction.PlanBelts(state, path);
                PlannedBeltRecord rec = HomeValleyConstruction.FindPlan(state, HomeValleyConstruction.BeltPlanPrefix + planId);
                if (key < 0 && rec != null)
                {
                    PlanSettings.ApplyToPlan(rec, first.Kind, first.S0, first.S1, first.S2);
                }
                result.PlanIds.Add(planId);
                foreach (PasteItem it in list)
                {
                    result.PlacedPieces++;
                    result.Pieces.Add(it.Id, it.Cell.X, it.Cell.Y, it.Rot & 3, it.Cell2.X, it.Cell2.Y, it.S0, it.S1, it.S2);
                }
            }
            if (result.Placed > 0)
            {
                Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstGhost);
            }
            return result;
        }

        private static void Fail(PlanApplyResult result, PasteItem it, GridReason reason)
        {
            result.Failed++;
            if (result.FirstFailure == null)
            {
                result.FirstFailure = reason;
                result.FirstFailureName = PlanEntries.NameOf(it.Id);
            }
        }

        // ── 拆掉一批件（撤销放置 / 重做拆除）────────────────────────────────────────

        /// <summary>最近一次 <see cref="RemovePieces"/>：拆掉的已建成件、取消的虚影格、没动的件（格子上已经不是这件东西 / 储罐里还有流体）。</summary>
        public static int LastRemovedBuilt { get; private set; }
        public static int LastCancelledPlanned { get; private set; }
        public static int LastRemoveSkipped { get; private set; }
        public static GridReason? LastRemoveSkipReason { get; private set; }

        /// <summary>
        /// 拆掉 <paramref name="pieces"/>（绝对坐标）里的物流件：还是虚影的取消规划（已到材料退回）；已经建成的立即拆掉、造价与带上物品全额返还
        /// （传送带与管线的拆除本来就是即时的，FG3-LOG-01 / 02）。只拆“还是这件东西”的格子（种类对得上；传送带不管等级——可能被升级过）；
        /// 储罐里还有流体的不拆（拆了会排空，不可逆，撤销不替玩家做这个决定，写明原因）。
        /// </summary>
        public static bool RemovePieces(CampaignState state, PlanEntryBlock pieces)
        {
            LastRemovedBuilt = 0;
            LastCancelledPlanned = 0;
            LastRemoveSkipped = 0;
            LastRemoveSkipReason = null;
            int n = PlanEntries.CountOf(pieces);
            if (state == null || n == 0)
            {
                return false;
            }
            var cells = new List<GridCell>(n);
            HomeGridMap map = HomeGridService.MapFor(state);
            for (int i = 0; i < n; i++)
            {
                PlanEntryKind k = PlanEntries.KindOf(pieces.Ids[i], out _);
                if (k == PlanEntryKind.Building || k == PlanEntryKind.Unknown)
                {
                    continue;
                }
                var c = new GridCell(pieces.Xs[i], pieces.Ys[i]);
                bool pipe = PlanEntries.IsPipeLayer(k);
                ushort layer = pipe ? map.GetPipe(c) : map.GetBelt(c);
                if (layer == 0)
                {
                    Skip(new GridReason(GridBlockReason.NoBuilding, "plan.reason.piece_gone", PlanEntries.NameOf(pieces.Ids[i]), c.X.ToString(), c.Y.ToString()));
                    continue;
                }
                if (!PlanSettings.TryReadPiece(state, c, out PlanEntryKind now, out _, out _, out _, out _) || now != k)
                {
                    Skip(new GridReason(GridBlockReason.NoBuilding, "plan.reason.piece_gone", PlanEntries.NameOf(pieces.Ids[i]), c.X.ToString(), c.Y.ToString()));
                    continue;
                }
                if (k == PlanEntryKind.Tank && !HomeValleyConstruction.IsPlannedMarker(layer) && PipeNetworkService.IsRunning
                    && PipeNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out PipeCellInfo tank) && tank.TankStockMl > 0)
                {
                    Skip(new GridReason(GridBlockReason.Busy, "plan.reason.tank_has_fluid", c.X.ToString(), c.Y.ToString()));
                    continue;
                }
                cells.Add(c);
            }
            if (cells.Count == 0)
            {
                return false;
            }
            GridOpResult r = HomeGridService.TryRemoveBelts(state, cells);
            if (r.Success)
            {
                LastRemovedBuilt = HomeGridService.LastBeltCount;
                LastCancelledPlanned = HomeGridService.LastPlannedBeltsCancelled;
            }
            return r.Success;
        }

        private static void Skip(GridReason reason)
        {
            LastRemoveSkipped++;
            LastRemoveSkipReason ??= reason;
        }
    }
}
