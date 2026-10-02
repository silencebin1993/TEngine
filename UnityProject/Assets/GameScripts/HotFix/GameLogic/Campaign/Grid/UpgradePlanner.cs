using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign.Grid
{
    /// <summary>FG3-LOG-07：升级规划里一组同种、同等级的物流件（一组一份升级规划；地下传送带一条一组）。</summary>
    public sealed class UpgradeGroup
    {
        public PlanEntryKind Kind;
        public string FromId;
        public string ToId;
        public int FromTier;
        public int ToTier;
        /// <summary>每件（地下传送带：每端）的差额。</summary>
        public int DiffPerUnit;
        public readonly List<GridCell> Cells = new List<GridCell>(16);
        public readonly List<int> Dirs = new List<int>(16);

        public int Pieces => Kind == PlanEntryKind.Underground || Kind == PlanEntryKind.PipeUnderground ? Cells.Count / 2 : Cells.Count;
        public int Cost => DiffPerUnit * Cells.Count;
    }

    /// <summary>FG3-LOG-07（FGR-LOG-010）：一次框选升级的规划——能升级的建筑与物流件、差额合计、不能升的件与第一条原因。</summary>
    public sealed class UpgradeBoxPlan
    {
        public GridCell Min;
        public GridCell Max;
        public readonly List<string> Buildings = new List<string>();
        public readonly List<string> BuildingTargets = new List<string>();
        public readonly List<UpgradeGroup> Groups = new List<UpgradeGroup>();
        public int Cost;
        /// <summary>FG4-ECO-11：框里建筑升级差额在废料之外要的材料合计（超控阵列 T2 / T3 的关键材料）。</summary>
        public readonly Economy.BuildMaterialTally Extras = new Economy.BuildMaterialTally();
        public int Refused;
        public GridReason? FirstRefusal;

        public int Count
        {
            get
            {
                int n = Buildings.Count;
                foreach (UpgradeGroup g in Groups)
                {
                    n += g.Pieces;
                }
                return n;
            }
        }

        public bool IsEmpty => Count == 0;
    }

    /// <summary>
    /// FG3-LOG-07（FG03 FGR-LOG-010“框选区域，把传送带、管线和建筑升级到已解锁的更高等级；原地升级，保留设置；生成施工任务，按新旧材料的差额收费”）。
    /// 升级路线在 fg.TbBuildUpgrade（数据驱动）：传送带 T1 → T2 → T3、地下传送带 T1 → T2 → T3、管线 T1 → T2、地下管线 T1 → T2（FG4-ECO-10，一对一起升）、电塔 T1 → T2。一次只升一级。
    /// 分流器 / 合流器 / 泵 / 储罐 / 阀门只有一种，不参与。只升级已建成的件（虚影还没建：直接选高一级的工具重放即可）。
    /// </summary>
    public static class UpgradePlanner
    {
        /// <summary>规划框选升级（闭区间，每边最多 grid.drag_max_cells 格）。O(建筑数 + 框内格数)，只在拖框换格时。</summary>
        public static UpgradeBoxPlan Plan(CampaignState state, GridCell a, GridCell b, UpgradeBoxPlan into = null)
        {
            UpgradeBoxPlan plan = into ?? new UpgradeBoxPlan();
            plan.Buildings.Clear();
            plan.BuildingTargets.Clear();
            plan.Groups.Clear();
            plan.Cost = 0;
            plan.Extras.Clear();
            plan.Refused = 0;
            plan.FirstRefusal = null;
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
            HomeGridMap map = HomeGridService.MapFor(state);
            foreach (BuildingRecord rec in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (rec == null || rec.RegionId != HomeValleyLayout.RegionId || HomeGridService.IsRelocationGhost(rec)
                    || !GridContent.TryGetBuilding(rec.BuildingTypeId, out BuildingGrid g))
                {
                    continue;
                }
                GridMath.FootprintBounds(new GridCell(rec.GridX, rec.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(rec.Rotation),
                    out GridCell bMin, out GridCell bMax);
                if (bMax.X < min.X || bMin.X > mx.X || bMax.Y < min.Y || bMin.Y > mx.Y)
                {
                    continue;
                }
                if (!GridContent.TryGetUpgrade(rec.BuildingTypeId, out _) && !Economy.BuildingOps.HasTiers(rec.BuildingTypeId))
                {
                    continue; // 没有升级路线 / 等级的建筑（大多数）不算“不能升”，不计入原因。
                }
                if (!HomeGridService.CanUpgradeNow(state, rec, out string to, out int toTier, out GridReason why))
                {
                    Refuse(plan, why);
                    continue;
                }
                plan.Buildings.Add(rec.BuildingId);
                plan.BuildingTargets.Add(to);
                plan.Cost += HomeGridService.UpgradeCost(rec, to, toTier, out _); // FG4-ECO-05：有等级的建筑（仓库 / 信号塔）按 fg.TbBuildingTier 的差额。
                plan.Extras.Add(toTier > 0 ? Economy.BuildMaterials.Upgrade(rec.BuildingTypeId, toTier) : null); // FG4-ECO-11：差额里的额外材料
            }
            var upgrading = new HashSet<long>();
            foreach (PlannedBeltRecord p in state.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                if (p?.Xs == null || !p.Upgrade)
                {
                    continue;
                }
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (p.CellState[i] == 0)
                    {
                        upgrading.Add(Key(p.Xs[i], p.Ys[i]));
                    }
                }
            }
            var byKey = new Dictionary<int, UpgradeGroup>();
            var pairsSeen = new HashSet<long>();
            for (int y = min.Y; y <= mx.Y; y++)
            {
                for (int x = min.X; x <= mx.X; x++)
                {
                    var c = new GridCell(x, y);
                    ushort belt = map.GetBelt(c);
                    if (belt != 0 && !HomeValleyConstruction.IsPlannedMarker(belt) && BeltNetworkService.IsRunning
                        && BeltNetworkService.TryGetPiece(c, out BeltNodeKind kind, out int tier))
                    {
                        if (kind == BeltNodeKind.Belt)
                        {
                            AddPiece(state, plan, byKey, upgrading, PlanEntryKind.Belt, "belt", tier, c, default, DirOf(c));
                        }
                        else if (kind == BeltNodeKind.UndergroundIn && BeltNetworkService.TryGetNode(c, out BeltNodeInfo u))
                        {
                            AddPiece(state, plan, byKey, upgrading, PlanEntryKind.Underground, "underground", tier, c, new GridCell(u.ExitX, u.ExitY), (int)u.Dir);
                        }
                    }
                    ushort pipe = map.GetPipe(c);
                    if (pipe != 0 && !HomeValleyConstruction.IsPlannedMarker(pipe) && PipeNetworkService.IsRunning
                        && PipeNetworkService.TryGetPiece(c, out PipePieceKind pk, out int pt))
                    {
                        if (pk == PipePieceKind.Pipe)
                        {
                            AddPiece(state, plan, byKey, upgrading, PlanEntryKind.Pipe, "pipe", pt, c, default, 0);
                        }
                        else if (pk == PipePieceKind.Underground)
                        {
                            // FG4-ECO-10（DEBT-FG4ECO04-05）：地下管线一对一起升（框住哪一口都行，一对只算一次）；没配对的口不升（跨度变大后可能和别的口配上、接错流体）。
                            if (!PipeNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out PipeCellInfo ui) || !ui.UndergroundLinked)
                            {
                                Refuse(plan, new GridReason(GridBlockReason.NoUpgrade, "plan.reason.pipe_underground_unpaired"));
                            }
                            else if (pairsSeen.Add(PairKey(c, new GridCell(ui.PartnerX, ui.PartnerY))))
                            {
                                AddPiece(state, plan, byKey, upgrading, PlanEntryKind.PipeUnderground, "pipe_underground", pt, c, new GridCell(ui.PartnerX, ui.PartnerY), 0);
                            }
                        }
                    }
                }
            }
            return plan;
        }

        private static int DirOf(GridCell c) =>
            BeltNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out BeltCellInfo info) ? (int)info.Dir : 0;

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        /// <summary>一对地下管线口的键（与先框到哪一口无关）。</summary>
        private static long PairKey(GridCell a, GridCell b)
        {
            bool aFirst = a.X < b.X || (a.X == b.X && a.Y <= b.Y);
            GridCell lo = aFirst ? a : b;
            GridCell hi = aFirst ? b : a;
            return Key(lo.X, lo.Y) * 1000003L ^ Key(hi.X, hi.Y);
        }

        private static void AddPiece(CampaignState state, UpgradeBoxPlan plan, Dictionary<int, UpgradeGroup> byKey, HashSet<long> upgrading,
            PlanEntryKind kind, string toolKind, int tier, GridCell cell, GridCell exit, int dir)
        {
            string from = PlanEntries.ToolIdFor(toolKind, tier);
            if (from == null || !GridContent.TryGetUpgrade(from, out string to) || !GridContent.TryGetTool(to, out BuildTool toTool)
                || !GridContent.TryGetTool(from, out BuildTool fromTool))
            {
                Refuse(plan, new GridReason(GridBlockReason.NoUpgrade, "plan.reason.no_upgrade_named", from != null ? PlanEntries.NameOf(from) : toolKind));
                return;
            }
            if (!BuildCatalog.IsUnlocked(state, toTool.UnlockRule))
            {
                Refuse(plan, new GridReason(GridBlockReason.UpgradeLocked, "plan.reason.upgrade_locked", PlanEntries.NameOf(to), toTool.UnlockHintKey));
                return;
            }
            if (upgrading.Contains(Key(cell.X, cell.Y)))
            {
                Refuse(plan, GridReason.Of(GridBlockReason.Upgrading));
                return;
            }
            int diff = Math.Max(0, toTool.ScrapPerCell - fromTool.ScrapPerCell);
            UpgradeGroup g;
            if (kind == PlanEntryKind.Underground || kind == PlanEntryKind.PipeUnderground)
            {
                g = new UpgradeGroup { Kind = kind, FromId = from, ToId = to, FromTier = tier, ToTier = toTool.Tier, DiffPerUnit = diff };
                g.Cells.Add(cell);
                g.Dirs.Add(dir);
                g.Cells.Add(exit);
                g.Dirs.Add(dir);
                plan.Groups.Add(g);
            }
            else
            {
                int key = ((int)kind << 8) | tier;
                if (!byKey.TryGetValue(key, out g))
                {
                    g = new UpgradeGroup { Kind = kind, FromId = from, ToId = to, FromTier = tier, ToTier = toTool.Tier, DiffPerUnit = diff };
                    byKey[key] = g;
                    plan.Groups.Add(g);
                }
                g.Cells.Add(cell);
                g.Dirs.Add(dir);
            }
            plan.Cost += kind == PlanEntryKind.Underground || kind == PlanEntryKind.PipeUnderground ? diff * 2 : diff;
        }

        private static void Refuse(UpgradeBoxPlan plan, GridReason why)
        {
            plan.Refused++;
            plan.FirstRefusal ??= why;
        }

        /// <summary>一组物流件的升级规划（HomeValleyConstruction.PlanUpgrade）。返回规划 ID。</summary>
        public static string PlanGroup(CampaignState state, UpgradeGroup g) =>
            HomeValleyConstruction.PlanUpgrade(state, g.Cells, g.Dirs, g.Kind == PlanEntryKind.Underground ? BeltNodeKind.UndergroundIn : BeltNodeKind.Belt,
                g.Kind == PlanEntryKind.Pipe || g.Kind == PlanEntryKind.PipeUnderground, g.FromTier, g.ToTier, g.DiffPerUnit,
                g.Kind == PlanEntryKind.PipeUnderground ? PipePieceKind.Underground : PipePieceKind.Pipe);
    }
}
