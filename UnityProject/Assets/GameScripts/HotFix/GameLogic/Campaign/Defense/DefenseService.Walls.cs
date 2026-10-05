using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using BinGames.Sim.Nav;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>FG6-DEF-02：敌方来路预览的结果（放置屏障时画在地面上）。</summary>
    public sealed class RoutePreview
    {
        /// <summary>每条来路一段（预览线用的格子序列）。</summary>
        public readonly List<List<GridCell>> Routes = new List<List<GridCell>>(4);
        /// <summary>每条来路放下前 / 放下后的代价（-1 = 不可达；代价 ÷ 10 ≈ 米）。</summary>
        public readonly List<int> Before = new List<int>(4);
        public readonly List<int> After = new List<int>(4);
        /// <summary>有来路会被完全堵死（放下后到不了核心，放下前到得了）。</summary>
        public bool Sealed;
        /// <summary>放下后来路最多变长多少米（不可达的不算）。</summary>
        public float MaxDetourMeters;
        /// <summary>有已知的来路（寻路镜像绑定且找到了来路起点）。</summary>
        public bool HasRoutes;

        public void Clear()
        {
            Routes.Clear();
            Before.Clear();
            After.Clear();
            Sealed = false;
            MaxDetourMeters = 0f;
            HasRoutes = false;
        }

        /// <summary>状态行的一句（当前语言）。</summary>
        public string Summary()
        {
            if (!HasRoutes)
            {
                return GameText.Get("build.wall.route_none");
            }
            if (Sealed)
            {
                return GameText.Get("build.wall.route_sealed");
            }
            return MaxDetourMeters >= 0.5f
                ? GameText.Format("build.wall.route_change", Mathf.RoundToInt(MaxDetourMeters))
                : GameText.Get("build.wall.route_same");
        }
    }

    /// <summary>FG6-DEF-02：拖拽铺设一段屏障 / 闸门的规划（逐格合法性、总造价、敌方来路预览）。</summary>
    public sealed class WallPlan
    {
        public string TypeId;
        public readonly List<GridCell> Cells = new List<GridCell>(32);
        public readonly List<bool> CellOk = new List<bool>(32);
        public int FirstBadIndex = -1;
        public GridReason? Reason;
        public int ScrapPerCell;
        public int TotalCost;
        public readonly RoutePreview Route = new RoutePreview();
        /// <summary>拖拽起点 / 终点（松开时复用这份规划，不再重算一遍预览）。</summary>
        public GridCell From;
        public GridCell To;
        /// <summary>FGR-LOG-012：这一段放下后机器到不了的建筑（显示名；闸门放行己方，不算）。</summary>
        public readonly List<string> CutsOff = new List<string>(4);
        public bool Ok => Reason == null && Cells.Count > 0;

        /// <summary>“放下后这些建筑机器到不了”的警告（没有时 null）。</summary>
        public string CutsOffWarning() =>
            CutsOff.Count == 0 ? null : GameText.Format("nav.build.unreachable_warning", string.Join(GameText.Language == GameLanguage.En ? ", " : "、", CutsOff));
    }

    public static partial class DefenseService
    {
        private static readonly List<BeltDir> DirScratch = new List<BeltDir>(32);
        private static readonly GridPlacementResult CellCheck = new GridPlacementResult();

        /// <summary>
        /// FGR-DEF-010（DEBT-FG3LOG01-03 拖拽建墙）：规划一次拖拽铺设——与传送带同一条自动转角路径（<see cref="HomeGridService.BuildBeltPath"/>），
        /// 逐格按放置规则校验（迷雾、地形、污染、占用、核心预留圈、解锁），长度上限 grid.drag_max_cells；再算敌方来路预览（<see cref="PreviewRoutes"/>，只在合法时算）。
        /// O(路径格数) + 一次 Burst 距离场；只在拖拽终点换格时调用。
        /// </summary>
        public static WallPlan PlanWall(CampaignState s, string typeId, GridCell from, GridCell to, WallPlan into = null, bool preview = true)
        {
            WallPlan plan = into ?? new WallPlan();
            plan.TypeId = typeId;
            plan.From = from;
            plan.To = to;
            plan.Cells.Clear();
            plan.CellOk.Clear();
            plan.FirstBadIndex = -1;
            plan.Reason = null;
            plan.Route.Clear();
            plan.CutsOff.Clear();
            plan.ScrapPerCell = HomeValleyLayout.BuildProfile.TryGetValue(typeId ?? string.Empty, out (int ScrapCost, float Seconds) cost) ? cost.ScrapCost : 0;
            HomeGridService.BuildBeltPath(from, to, BeltDir.North, plan.Cells, DirScratch);
            plan.TotalCost = plan.ScrapPerCell * plan.Cells.Count;
            if (s == null)
            {
                plan.Reason = GridReason.Of(GridBlockReason.NoRegion);
                return plan;
            }
            if (!DefenseCatalog.IsDraggable(typeId))
            {
                plan.Reason = GridReason.Of(GridBlockReason.UnknownType);
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
                plan.FirstBadIndex = max;
                return plan;
            }
            for (int i = 0; i < plan.Cells.Count; i++)
            {
                HomeGridService.ValidatePlacement(s, typeId, plan.Cells[i], 0, into: CellCheck);
                plan.CellOk.Add(CellCheck.Ok);
                if (!CellCheck.Ok && plan.FirstBadIndex < 0)
                {
                    plan.FirstBadIndex = i;
                    plan.Reason = CellCheck.Reasons[0];
                }
            }
            if (plan.Reason == null && preview)
            {
                PreviewRoutes(s, plan.Cells, plan.Route);
                // 复审修复（P2，FGR-LOG-012）：整段放下后机器到不了哪些建筑（只警告，不阻止）；闸门放行己方单位，不检查。
                if (DefenseCatalog.KindOf(typeId) != DefenseKind.Gate)
                {
                    NavService.PlacementCutsOff(s, plan.Cells, plan.CutsOff);
                }
            }
            return plan;
        }

        /// <summary>
        /// 松开鼠标时铺下整段（全有或全无：任何一格不合法就一格都不放，原因写明是哪一格为什么）。成功时整段是撤销栈里的一步（<see cref="PlanHistory"/> 批量步）；
        /// 放下的是虚影，机器取料施工（库存不够不拦，HUD 写还差多少，与传送带一致）。返回放下的座数。
        /// </summary>
        public static GridOpResult TryPlaceWall(CampaignState s, string typeId, GridCell from, GridCell to, out int placed)
        {
            placed = 0;
            WallPlan plan = PlanWall(s, typeId, from, to, preview: false); // 只校验：预览已在拖拽中算过（复审修复：松手时不再算两遍来路）
            if (!plan.Ok)
            {
                return GridOpResult.Fail(plan.Reason ?? GridReason.Of(GridBlockReason.UnknownType));
            }
            GridOpResult last = default;
            PlanHistory.BeginStep(s, PlanStepKind.Batch);
            try
            {
                foreach (GridCell c in plan.Cells)
                {
                    GridOpResult r = PlanHistory.Place(s, typeId, c, 0);
                    if (r.Success)
                    {
                        placed++;
                        last = r;
                    }
                }
            }
            finally
            {
                PlanHistory.EndStep(s);
            }
            if (placed == 0)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.UnknownType));
            }
            Touch();
            return last;
        }

        // ─────────────────────────────── 敌方来路预览（FG06 第 4 节“放置屏障时预览对流场的影响”）───────────────────────────────

        private static readonly List<int2> Goals = new List<int2>(64);
        private static readonly List<int2> Extra = new List<int2>(64);
        private static readonly List<int2> Entries = new List<int2>(8);
        private static readonly List<int2> PointsBefore = new List<int2>(256);
        private static readonly List<int2> PointsAfter = new List<int2>(256);
        private static readonly HashSet<GridCell> NewCellSet = new HashSet<GridCell>();
        private static readonly List<int2> Planned = new List<int2>(64);
        private static readonly HashSet<GridCell> PlannedSet = new HashSet<GridCell>();
        private static readonly List<GridCell> PlannedFoot = new List<GridCell>(4);

        /// <summary>已规划、还没建成（等料 / 施工中）的屏障 / 闸门——来路预览按建成算（敌方类别下两种都挡）。被摧毁的不算（要玩家重建）。</summary>
        private static bool IsPlannedBlocker(BuildingRecord b) =>
            b != null && b.RegionId == HomeValleyLayout.RegionId && DefenseCatalog.BlocksMovement(b.BuildingTypeId)
            && (b.ConstructionState == BuildingConstructionState.Planned || b.ConstructionState == BuildingConstructionState.MaterialReserved
                || b.ConstructionState == BuildingConstructionState.Building);

        /// <summary>自检读：最近一次来路预览按“规划中”计入的虚影格数。</summary>
        public static int LastPlannedCells => Planned.Count;

        /// <summary>
        /// 假设把 <paramref name="newCells"/> 也挡上（新屏障），比较敌方来路放下前 / 放下后的变化。来路起点 = 预览框（家园建筑外接矩形 + defense.preview.margin_cells）边上、
        /// 朝着最近的几个敌方据点方向的可走格（没有已知据点时取东南西北）；终点 = 核心外一圈。距离场与下坡追踪在 Burst（<see cref="NavService.FlowRoutes"/>）。
        /// 放下后不可达、放下前可达 = 堵死（预览线画放下前的来路到第一段新墙为止，敌人会攻击挡路的墙）。只在预览换格 / 换朝向时调用。经 NavService 碰寻路内核。
        /// </summary>
        public static void PreviewRoutes(CampaignState s, IReadOnlyList<GridCell> newCells, RoutePreview into)
        {
            into.Clear();
            if (s == null || !NavService.IsBound || !ReferenceEquals(s, NavService.BoundState)
                || !HomeGridService.TryGetCoreBounds(s, out GridCell coreMin, out GridCell coreMax))
            {
                return;
            }
            NavService.SyncGridChanges();
            int minX = coreMin.X, minY = coreMin.Y, maxX = coreMax.X, maxY = coreMax.Y;
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || !GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid bg))
                {
                    continue;
                }
                GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), bg.FootprintW, bg.FootprintH, GridMath.NormalizeRotation(b.Rotation), out GridCell bmin, out GridCell bmax);
                minX = Math.Min(minX, bmin.X);
                minY = Math.Min(minY, bmin.Y);
                maxX = Math.Max(maxX, bmax.X);
                maxY = Math.Max(maxY, bmax.Y);
            }
            // 复审修复（P1）：同一轮规划里先放下、还在等机器施工的屏障 / 闸门虚影，“放下前 / 放下后”两边都按建成算（敌方类别下闸门同样挡路）——
            // 正常玩法是先分几次拖出一整圈虚影再施工，补最后一个缺口时要能提示“会完全堵死”。被摧毁的（受损）不算：要玩家重建才会再挡。
            Planned.Clear();
            PlannedSet.Clear();
            int plannedKey = 17;
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (!IsPlannedBlocker(b))
                {
                    continue;
                }
                PlannedFoot.Clear();
                HomeGridService.FootprintOf(b, PlannedFoot);
                foreach (GridCell c in PlannedFoot)
                {
                    if (PlannedSet.Add(c))
                    {
                        Planned.Add(new int2(c.X, c.Y));
                        plannedKey = HashCode.Combine(plannedKey, c.X, c.Y);
                    }
                }
            }
            Extra.Clear();
            Extra.AddRange(Planned);
            NewCellSet.Clear();
            if (newCells != null)
            {
                foreach (GridCell c in newCells)
                {
                    if (!PlannedSet.Contains(c))
                    {
                        Extra.Add(new int2(c.X, c.Y));
                    }
                    NewCellSet.Add(c);
                    minX = Math.Min(minX, c.X);
                    minY = Math.Min(minY, c.Y);
                    maxX = Math.Max(maxX, c.X);
                    maxY = Math.Max(maxY, c.Y);
                }
            }
            int margin = DefenseCatalog.PreviewMarginCells;
            var min = new int2(minX - margin, minY - margin);
            var max = new int2(maxX + margin, maxY + margin);
            Goals.Clear();
            for (int x = coreMin.X - 1; x <= coreMax.X + 1; x++)
            {
                Goals.Add(new int2(x, coreMin.Y - 1));
                Goals.Add(new int2(x, coreMax.Y + 1));
            }
            for (int y = coreMin.Y; y <= coreMax.Y; y++)
            {
                Goals.Add(new int2(coreMin.X - 1, y));
                Goals.Add(new int2(coreMax.X + 1, y));
            }
            Entries.Clear();
            var core = new Vector2((coreMin.X + coreMax.X) * 0.5f, (coreMin.Y + coreMax.Y) * 0.5f);
            foreach (Vector2 dir in SourceDirections(s, core))
            {
                if (TryEntryCell(core, dir, min, max, out int2 e) && !Entries.Contains(e))
                {
                    Entries.Add(e);
                }
            }
            if (Entries.Count == 0)
            {
                return;
            }
            int n = Entries.Count;
            var offB = new int[n];
            var cntB = new int[n];
            var costB = new int[n];
            var offA = new int[n];
            var cntA = new int[n];
            var costA = new int[n];
            // “放下前”的来路只取决于寻路镜像与预览框：镜像没推进过新区块、框与来路起点都没变时沿用上一次（拖拽时每换一格只算“放下后”一次）。
            int beforeKey = HashCode.Combine(HashCode.Combine(NavService.PushedChunks, min.x, min.y, max.x, max.y, EntriesKey(), NavService.MirrorIdentity), plannedKey, Planned.Count);
            if (beforeKey != _beforeKey || _beforeCost.Length != n)
            {
                _beforeOff = new int[n];
                _beforeCnt = new int[n];
                _beforeCost = new int[n];
                NavService.FlowRoutes(NavConst.ClassHostile, min, max, Goals, Planned.Count > 0 ? Planned : null, Entries, PointsBefore, _beforeOff, _beforeCnt, _beforeCost);
                _beforeKey = beforeKey;
                BeforeComputes++;
            }
            Array.Copy(_beforeOff, offB, n);
            Array.Copy(_beforeCnt, cntB, n);
            Array.Copy(_beforeCost, costB, n);
            NavService.FlowRoutes(NavConst.ClassHostile, min, max, Goals, Extra, Entries, PointsAfter, offA, cntA, costA);
            for (int i = 0; i < n; i++)
            {
                if (costB[i] < 0 && costA[i] < 0)
                {
                    continue; // 放下前就到不了（例如来路起点在湖心）：这条不算来路
                }
                into.HasRoutes = true;
                into.Before.Add(costB[i]);
                into.After.Add(costA[i]);
                var line = new List<GridCell>(Math.Max(cntA[i], cntB[i]));
                if (costA[i] >= 0)
                {
                    for (int p = 0; p < cntA[i]; p++)
                    {
                        int2 c = PointsAfter[offA[i] + p];
                        line.Add(new GridCell(c.x, c.y));
                    }
                    if (costB[i] >= 0)
                    {
                        into.MaxDetourMeters = Mathf.Max(into.MaxDetourMeters, (costA[i] - costB[i]) / (float)NavConst.Ortho);
                    }
                }
                else
                {
                    into.Sealed = true;
                    for (int p = 0; p < cntB[i]; p++)
                    {
                        int2 c = PointsBefore[offB[i] + p];
                        var gc = new GridCell(c.x, c.y);
                        line.Add(gc);
                        if (NewCellSet.Contains(gc))
                        {
                            break; // 画到第一段新墙为止：敌人会在这里攻击挡路的墙
                        }
                    }
                }
                into.Routes.Add(line);
            }
            if (into.HasRoutes)
            {
                GuidanceHooks.Raise(GuidanceHooks.DefenseRouteFirstPreview);
            }
        }

        private static int _beforeKey;
        private static int[] _beforeOff = Array.Empty<int>();
        private static int[] _beforeCnt = Array.Empty<int>();
        private static int[] _beforeCost = Array.Empty<int>();

        /// <summary>自检读：“放下前”的来路真正重算的次数（拖拽中应只在镜像变化时重算）。</summary>
        public static int BeforeComputes { get; private set; }

        private static int EntriesKey()
        {
            int h = Entries.Count;
            foreach (int2 e in Entries)
            {
                h = HashCode.Combine(h, e.x, e.y);
            }
            return h;
        }

        /// <summary>敌人从哪些方向来：最近的几个敌方据点（按到核心的距离、再按 ID 排序，确定性；B25 按种子生成的据点）；没有据点时东南西北。</summary>
        private static List<Vector2> SourceDirections(CampaignState s, Vector2 core)
        {
            var dirs = new List<Vector2>(DefenseCatalog.PreviewMaxRoutes);
            OutpostRecord[] outposts = s.Raids?.Outposts ?? Array.Empty<OutpostRecord>();
            var sorted = new List<OutpostRecord>(outposts.Length);
            foreach (OutpostRecord o in outposts)
            {
                if (o != null)
                {
                    sorted.Add(o);
                }
            }
            sorted.Sort((a, b) =>
            {
                float da = (new Vector2(a.CellX, a.CellY) - core).sqrMagnitude;
                float db = (new Vector2(b.CellX, b.CellY) - core).sqrMagnitude;
                int c = da.CompareTo(db);
                return c != 0 ? c : string.CompareOrdinal(a.OutpostId, b.OutpostId);
            });
            foreach (OutpostRecord o in sorted)
            {
                Vector2 d = new Vector2(o.CellX, o.CellY) - core;
                if (d.sqrMagnitude < 1e-4f)
                {
                    continue;
                }
                d.Normalize();
                bool near = false;
                foreach (Vector2 e in dirs)
                {
                    near |= Vector2.Dot(e, d) > 0.97f; // 同一方向的据点只算一条来路
                }
                if (!near)
                {
                    dirs.Add(d);
                }
                if (dirs.Count >= DefenseCatalog.PreviewMaxRoutes)
                {
                    break;
                }
            }
            if (dirs.Count == 0)
            {
                dirs.Add(Vector2.up);
                dirs.Add(Vector2.right);
                dirs.Add(Vector2.down);
                dirs.Add(Vector2.left);
            }
            return dirs;
        }

        /// <summary>从核心沿 <paramref name="dir"/> 射到预览框边上，再沿框边就近找一格敌方可走的格作为来路起点（最多找半圈）。</summary>
        private static bool TryEntryCell(Vector2 core, Vector2 dir, int2 min, int2 max, out int2 cell)
        {
            cell = default;
            float tx = dir.x > 1e-4f ? (max.x - core.x) / dir.x : dir.x < -1e-4f ? (min.x - core.x) / dir.x : float.MaxValue;
            float ty = dir.y > 1e-4f ? (max.y - core.y) / dir.y : dir.y < -1e-4f ? (min.y - core.y) / dir.y : float.MaxValue;
            float t = Mathf.Min(tx, ty);
            if (t == float.MaxValue || t <= 0f)
            {
                return false;
            }
            Vector2 hit = core + dir * t;
            var start = new int2(Mathf.Clamp(Mathf.RoundToInt(hit.x), min.x, max.x), Mathf.Clamp(Mathf.RoundToInt(hit.y), min.y, max.y));
            int perimeter = 2 * ((max.x - min.x) + (max.y - min.y));
            int idx0 = PerimeterIndex(start, min, max);
            for (int k = 0; k <= perimeter / 2; k++)
            {
                for (int sgn = 0; sgn < 2; sgn++)
                {
                    int idx = ((idx0 + (sgn == 0 ? k : -k)) % perimeter + perimeter) % perimeter;
                    int2 c = PerimeterCell(idx, min, max);
                    if (NavService.PassableNow(c.x, c.y, NavConst.ClassHostile))
                    {
                        cell = c;
                        return true;
                    }
                    if (k == 0)
                    {
                        break;
                    }
                }
            }
            return false;
        }

        private static int PerimeterIndex(int2 c, int2 min, int2 max)
        {
            int w = max.x - min.x;
            int h = max.y - min.y;
            if (c.y == min.y)
            {
                return c.x - min.x;
            }
            if (c.x == max.x)
            {
                return w + (c.y - min.y);
            }
            if (c.y == max.y)
            {
                return w + h + (max.x - c.x);
            }
            return 2 * w + h + (max.y - c.y);
        }

        private static int2 PerimeterCell(int idx, int2 min, int2 max)
        {
            int w = max.x - min.x;
            int h = max.y - min.y;
            if (idx < w)
            {
                return new int2(min.x + idx, min.y);
            }
            idx -= w;
            if (idx < h)
            {
                return new int2(max.x, min.y + idx);
            }
            idx -= h;
            if (idx < w)
            {
                return new int2(max.x - idx, max.y);
            }
            idx -= w;
            return new int2(min.x, max.y - idx);
        }
    }
}
