using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>顶棚拖拽预览里的一块（顶棚格坐标）。</summary>
    public struct RoofPlanBlock
    {
        public int Bx;
        public int By;
        /// <summary>0 = 新铺；1 = 已有顶棚（已建成或施工中，跳过）；2 = 不能铺（未探索 / 超出世界）。</summary>
        public byte State;
    }

    /// <summary>一次顶棚拖拽的规划（建造模式的预览与放下共用）。</summary>
    public sealed class RoofPlan
    {
        public readonly List<RoofPlanBlock> Blocks = new List<RoofPlanBlock>(16);
        public int New;
        public int Existing;
        public int Fogged;
        public int Outside;
        public int ScrapPerBlock;
        public int TotalScrap => New * ScrapPerBlock;
        /// <summary>不能放下的原因（文本键已格式化）；null = 可以放下。</summary>
        public string Reason;
        public bool Ok => Reason == null;
        public GridCell Min;
        public GridCell Max;

        public void Clear()
        {
            Blocks.Clear();
            New = Existing = Fogged = Outside = 0;
            Reason = null;
        }
    }

    /// <summary>
    /// FG7-ENV-03（FG07 FGR-ENV-030；FG03 FGR-LOG-028“顶棚覆盖下不受影响”）：顶棚。
    /// - 4×4 一块（env.roof.block_cells），对齐顶棚格（格坐标 ÷ 4 向下取整）：拖出的矩形碰到的每一块都铺，已有的跳过，块与块天然不重叠。
    /// - 不占地：顶棚铺在建筑、传送带、管线上方，不进格网任何层、不挡路、不挡放置；下面照常建造、走路、操作（FGR-ENV-030“玩家可以看到并操作下面的东西”）。
    /// - 施工与传送带规划同一条流程（<see cref="HomeValleyConstruction"/> 的“传送带式规划”：机器取料、逐块建成、取消 / 拆除全额返还、静默夜暂缓），记录存天气域
    ///   （<see cref="RoofState"/>，与 <see cref="GridState.PlannedBelts"/> 分开：格子上的传送带 / 管线规划逻辑看不到顶棚）。
    /// - 覆盖：一格在某块已建成的顶棚里 = 不受酸雨与沙暴影响；一座建筑整座在顶棚下才算（占地每格都覆盖）。传送带内核按坐标登记覆盖（派生量，<see cref="SyncBeltCover"/>）。
    /// - 研发门槛：建造菜单工具 roof 的 unlockRule（研究“环境 · 顶棚”）。
    /// 查询 O(1)（块坐标哈希集）；放下 / 拆除 O(块数)；不每帧遍历。
    /// </summary>
    public static class RoofService
    {
        public const string ToolId = "roof";

        /// <summary>顶棚建成 / 拆除 / 规划变化 / 隐藏切换时 +1（画面、叠加层据此重建）。</summary>
        public static int Revision { get; private set; } = 1;

        public static int LastPlaced { get; private set; }
        public static int LastRemoved { get; private set; }
        public static int LastCancelled { get; private set; }
        public static string LastMessage { get; private set; } = string.Empty;

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static int BlockCells => Math.Max(1, (int)Math.Round(Tuning("env.roof.block_cells", 4f)));
        public static int MaxBlocksPerDrag => Math.Max(1, (int)Math.Round(Tuning("env.roof.max_blocks_per_drag", 64f)));
        public static float SecondsPerBlock => Math.Max(0.1f, Tuning("env.roof.build_seconds_per_block", 3f));
        public static int ScrapPerBlock => GridContent.TryGetTool(ToolId, out BuildTool t) ? Math.Max(1, t.ScrapPerCell) : 8;

        public static RoofState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            s.Weather ??= new WeatherState();
            return s.Weather.Roofs ??= new RoofState();
        }

        public static int FloorDiv(int v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);
        public static int BlockOf(int v) => FloorDiv(v, BlockCells);
        private static long Key(int bx, int by) => ((long)bx << 32) ^ (uint)by;

        // ── 索引（块坐标 → 已建成 / 规划中）：存档数组换了引用（读档 / 改动）时重建 ──

        private static readonly HashSet<long> Built = new HashSet<long>();
        private static readonly HashSet<long> Planned = new HashSet<long>();
        private static CampaignState _indexed;
        private static RoofRecord[] _builtRef;
        private static PlannedBeltRecord[] _plansRef;
        private static int _plansRevision = -1;

        private static void EnsureIndex(CampaignState s)
        {
            RoofState r = StateOf(s);
            if (r == null)
            {
                return;
            }
            if (ReferenceEquals(s, _indexed) && ReferenceEquals(r.Built, _builtRef) && ReferenceEquals(r.Plans, _plansRef) && _plansRevision == HomeValleyConstruction.Revision)
            {
                return;
            }
            Built.Clear();
            Planned.Clear();
            foreach (RoofRecord b in r.Built ?? Array.Empty<RoofRecord>())
            {
                if (b != null)
                {
                    Built.Add(Key(b.Bx, b.By));
                }
            }
            foreach (PlannedBeltRecord p in r.Plans ?? Array.Empty<PlannedBeltRecord>())
            {
                if (p?.Xs == null)
                {
                    continue;
                }
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (i < p.CellState.Length && p.CellState[i] == 0)
                    {
                        Planned.Add(Key(BlockOf(p.Xs[i]), BlockOf(p.Ys[i])));
                    }
                }
            }
            _indexed = s;
            _builtRef = r.Built;
            _plansRef = r.Plans;
            _plansRevision = HomeValleyConstruction.Revision;
        }

        /// <summary>这一块有没有已建成的顶棚。</summary>
        public static bool HasBlock(CampaignState s, int bx, int by)
        {
            if (s?.Weather?.Roofs == null)
            {
                return false;
            }
            EnsureIndex(s);
            return Built.Contains(Key(bx, by));
        }

        /// <summary>这一块有没有施工中的顶棚规划。</summary>
        public static bool IsPlannedBlock(CampaignState s, int bx, int by)
        {
            if (s?.Weather?.Roofs == null)
            {
                return false;
            }
            EnsureIndex(s);
            return Planned.Contains(Key(bx, by));
        }

        /// <summary>这一格在已建成的顶棚下（不受酸雨与沙暴影响）。O(1)。</summary>
        public static bool IsCovered(CampaignState s, GridCell cell) => HasBlock(s, BlockOf(cell.X), BlockOf(cell.Y));

        private static readonly List<GridCell> FootScratch = new List<GridCell>(25);

        /// <summary>一座建筑整座都在顶棚下（占地每格都被已建成的顶棚覆盖）。O(占地格数)。</summary>
        public static bool IsBuildingCovered(CampaignState s, BuildingRecord b)
        {
            if (b == null || s?.Weather?.Roofs == null || (s.Weather.Roofs.Built?.Length ?? 0) == 0)
            {
                return false;
            }
            HomeGridService.FootprintOf(b, FootScratch);
            if (FootScratch.Count == 0)
            {
                return IsCovered(s, new GridCell(b.GridX, b.GridY));
            }
            foreach (GridCell c in FootScratch)
            {
                if (!IsCovered(s, c))
                {
                    return false;
                }
            }
            return true;
        }

        public static int BuiltCount(CampaignState s) => s?.Weather?.Roofs?.Built?.Length ?? 0;

        public static int PlannedCount(CampaignState s)
        {
            if (s?.Weather?.Roofs == null)
            {
                return 0;
            }
            EnsureIndex(s);
            return Planned.Count;
        }

        /// <summary>全部顶棚块（已建成 + 施工中；画面与叠加层用）。O(块数)。</summary>
        public static void CollectBlocks(CampaignState s, List<RoofPlanBlock> into)
        {
            into.Clear();
            RoofState r = s?.Weather?.Roofs;
            if (r == null)
            {
                return;
            }
            foreach (RoofRecord b in r.Built ?? Array.Empty<RoofRecord>())
            {
                if (b != null)
                {
                    into.Add(new RoofPlanBlock { Bx = b.Bx, By = b.By, State = 0 });
                }
            }
            foreach (PlannedBeltRecord p in r.Plans ?? Array.Empty<PlannedBeltRecord>())
            {
                if (p?.Xs == null)
                {
                    continue;
                }
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (i < p.CellState.Length && p.CellState[i] == 0)
                    {
                        into.Add(new RoofPlanBlock { Bx = BlockOf(p.Xs[i]), By = BlockOf(p.Ys[i]), State = 1 });
                    }
                }
            }
        }

        /// <summary>顶棚工具解锁了没有（建造菜单工具 roof 的 unlockRule = 研究“环境 · 顶棚”）。</summary>
        public static bool IsUnlocked(CampaignState s) => GridContent.TryGetTool(ToolId, out BuildTool t) && BuildCatalog.IsUnlocked(s, t.UnlockRule);

        // ── 规划（建造模式拖拽）──

        /// <summary>拖出的矩形 [a, b] 碰到的顶棚块：新铺 / 已有（跳过）/ 不能铺（未探索、超出世界），总造价与第一条原因。O(块数 × 16)。</summary>
        public static RoofPlan PlanBox(CampaignState s, GridCell a, GridCell b, RoofPlan into = null)
        {
            RoofPlan plan = into ?? new RoofPlan();
            plan.Clear();
            plan.ScrapPerBlock = ScrapPerBlock;
            plan.Min = new GridCell(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
            plan.Max = new GridCell(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            if (s == null)
            {
                plan.Reason = GameText.Get("env.roof.reason.not_home");
                return plan;
            }
            if (!IsUnlocked(s))
            {
                plan.Reason = GameText.Get("env.roof.reason.locked");
                return plan;
            }
            int bx0 = BlockOf(plan.Min.X), bx1 = BlockOf(plan.Max.X), by0 = BlockOf(plan.Min.Y), by1 = BlockOf(plan.Max.Y);
            long total = (long)(bx1 - bx0 + 1) * (by1 - by0 + 1);
            if (total > MaxBlocksPerDrag)
            {
                plan.Reason = GameText.Format("env.roof.reason.limit", MaxBlocksPerDrag, total);
                return plan;
            }
            EnsureIndex(s);
            int n = BlockCells;
            int worldLimit = GridContent.TuningInt("world.coord_limit");
            HomeGridMap map = HomeGridService.MapFor(s);
            for (int by = by0; by <= by1; by++)
            {
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    byte st = 0;
                    if (Built.Contains(Key(bx, by)) || Planned.Contains(Key(bx, by)))
                    {
                        st = 1;
                        plan.Existing++;
                    }
                    else
                    {
                        bool outside = false;
                        bool fog = false;
                        for (int dy = 0; dy < n && !outside; dy++)
                        {
                            for (int dx = 0; dx < n; dx++)
                            {
                                var c = new GridCell(bx * n + dx, by * n + dy);
                                if (!WorldCoord.WithinLimit(c, worldLimit))
                                {
                                    outside = true;
                                    break;
                                }
                                HomeGridMap.Chunk chunk = map.ChunkAt(c, out int idx);
                                if (chunk == null || chunk.Explored[idx] == 0)
                                {
                                    fog = true;
                                }
                            }
                        }
                        if (outside)
                        {
                            st = 2;
                            plan.Outside++;
                        }
                        else if (fog)
                        {
                            st = 2;
                            plan.Fogged++;
                        }
                        else
                        {
                            plan.New++;
                        }
                    }
                    plan.Blocks.Add(new RoofPlanBlock { Bx = bx, By = by, State = st });
                }
            }
            if (plan.Outside > 0)
            {
                plan.Reason = GameText.Get("env.roof.reason.world");
            }
            else if (plan.Fogged > 0)
            {
                plan.Reason = GameText.Format("env.roof.reason.fog", plan.Fogged);
            }
            else if (plan.New == 0)
            {
                plan.Reason = GameText.Get("env.roof.reason.none");
            }
            return plan;
        }

        /// <summary>拖拽信息行：“顶棚：新铺 N 块（已有 M 块跳过）· 需要 X 废料”，不能放时接原因。</summary>
        public static string PlanText(RoofPlan p) =>
            p == null ? string.Empty
            : GameText.Format("env.roof.plan_status", p.New, p.Existing, p.TotalScrap) + (p.Ok ? string.Empty : "\n" + GameText.Format("ui.build.invalid", p.Reason));

        /// <summary>
        /// 放下顶棚虚影（全有或全无：有块在未探索处 / 超出世界 / 超过上限就整次拒绝并写明原因）：记一份顶棚规划（每块一格，施工站 = 这一块里第一处没有建筑的格），
        /// 开一张施工单（机器取料施工；材料不够也能放，虚影等材料）。返回放下的块数（0 = 没放，原因在 <see cref="LastMessage"/>）。
        /// </summary>
        public static int PlaceBox(CampaignState s, GridCell a, GridCell b)
        {
            RoofPlan plan = PlanBox(s, a, b);
            LastPlaced = 0;
            if (!plan.Ok)
            {
                LastMessage = plan.Reason;
                return 0;
            }
            RoofState r = StateOf(s);
            int n = BlockCells;
            var xs = new List<int>(plan.New);
            var ys = new List<int>(plan.New);
            foreach (RoofPlanBlock blk in plan.Blocks)
            {
                if (blk.State != 0)
                {
                    continue;
                }
                GridCell site = SiteCell(s, blk.Bx, blk.By, n);
                xs.Add(site.X);
                ys.Add(site.Y);
            }
            string planId = "r" + r.NextPlanSerial.ToString(CultureInfo.InvariantCulture);
            r.NextPlanSerial++;
            var rec = new PlannedBeltRecord
            {
                PlanId = planId,
                Tier = 0,
                ScrapPerCell = plan.ScrapPerBlock,
                Xs = xs.ToArray(),
                Ys = ys.ToArray(),
                Dirs = new int[xs.Count],
                CellState = new int[xs.Count],
                RatioL = 1,
                RatioR = 1,
                Roof = true,
            };
            var list = new List<PlannedBeltRecord>(r.Plans ?? Array.Empty<PlannedBeltRecord>()) { rec };
            r.Plans = list.ToArray();
            HomeValleyWorkOrders.CreateConstructionOrder(s, HomeValleyConstruction.RoofPlanPrefix + planId, rec.ScrapPerCell * xs.Count, SecondsPerBlock);
            HomeValleyConstruction.Touch();
            LastPlaced = xs.Count;
            LastMessage = GameText.Format("env.roof.placed", xs.Count, rec.ScrapPerCell * xs.Count);
            GuidanceHooks.Raise(GuidanceHooks.EnvFirstRoof);
            Revision++;
            return xs.Count;
        }

        /// <summary>机器施工站：这一块里第一处（行优先）没有建筑占着、地形可走的格；都不行就用块中心（机器走到身边即可施工）。</summary>
        private static GridCell SiteCell(CampaignState s, int bx, int by, int n)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            for (int dy = 0; dy < n; dy++)
            {
                for (int dx = 0; dx < n; dx++)
                {
                    var c = new GridCell(bx * n + dx, by * n + dy);
                    HomeGridMap.Chunk chunk = map.ChunkAt(c, out int idx);
                    if (chunk == null || chunk.Occupancy[idx] != 0)
                    {
                        continue;
                    }
                    GridTerrain t = GridContent.TerrainByCode(chunk.Terrain[idx]);
                    if (t != null && t.Buildable == 1)
                    {
                        return c;
                    }
                }
            }
            return new GridCell(bx * n + n / 2, by * n + n / 2);
        }

        /// <summary>施工流程建成一块（<see cref="HomeValleyConstruction"/> 逐格施工调用）：记进已建成、登记传送带覆盖。这一块已经有顶棚时返回 false（材料留给下一块）。</summary>
        public static bool CommitBlock(CampaignState s, PlannedBeltRecord p, int i)
        {
            RoofState r = StateOf(s);
            if (r == null || p == null || i < 0 || i >= p.Xs.Length)
            {
                return false;
            }
            int bx = BlockOf(p.Xs[i]), by = BlockOf(p.Ys[i]);
            EnsureIndex(s);
            if (Built.Contains(Key(bx, by)))
            {
                return false;
            }
            var list = new List<RoofRecord>(r.Built ?? Array.Empty<RoofRecord>()) { new RoofRecord { Bx = bx, By = by } };
            r.Built = list.ToArray();
            p.CellState[i] = 1; // 这一块建成（施工流程按 CellState 找下一块）
            SetBlockCover(bx, by, true);
            Revision++;
            return true;
        }

        /// <summary>施工流程移走一份顶棚规划（完工 / 取消）。</summary>
        public static void RemovePlan(CampaignState s, PlannedBeltRecord p)
        {
            RoofState r = StateOf(s);
            if (r?.Plans == null || p == null)
            {
                return;
            }
            var list = new List<PlannedBeltRecord>(r.Plans.Length);
            foreach (PlannedBeltRecord x in r.Plans)
            {
                if (x != null && !ReferenceEquals(x, p))
                {
                    list.Add(x);
                }
            }
            r.Plans = list.ToArray();
            Revision++;
        }

        /// <summary>
        /// 拆除模式框选（FGR-LOG-007 拆除全额返还）：框 [a, b] 完整包住的已建成顶棚块拆掉、造价全额返还；完整包住的施工中顶棚块取消（多出来的已到材料退回，
        /// 整份规划都取消时施工单一并取消、已到材料全额退回）。部分碰到的块不动（框一片拆传送带时不会顺手拆掉头顶的顶棚）。返回拆掉 + 取消的块数。
        /// </summary>
        public static int DemolishBox(CampaignState s, GridCell a, GridCell b)
        {
            LastRemoved = LastCancelled = 0;
            RoofState r = StateOf(s);
            if (r == null)
            {
                return 0;
            }
            int n = BlockCells;
            int minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            bool Inside(int bx, int by) => bx * n >= minX && bx * n + n - 1 <= maxX && by * n >= minY && by * n + n - 1 <= maxY;
            int scrap = ScrapPerBlock;
            var keep = new List<RoofRecord>(r.Built?.Length ?? 0);
            foreach (RoofRecord blk in r.Built ?? Array.Empty<RoofRecord>())
            {
                if (blk != null && Inside(blk.Bx, blk.By))
                {
                    SetBlockCover(blk.Bx, blk.By, false);
                    LastRemoved++;
                    continue;
                }
                if (blk != null)
                {
                    keep.Add(blk);
                }
            }
            if (LastRemoved > 0)
            {
                r.Built = keep.ToArray();
                var at = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
                HomeValleyConstruction.ReturnMaterials(s, at, CampaignEconomyLedger.ResourceScrap, LastRemoved * scrap, "roof-demolish:" + minX + "," + minY + ":" + GameClock.Ticks);
                HomeValleyConstruction.RecordDemolishRefund(s, LastRemoved * scrap);
            }
            foreach (PlannedBeltRecord p in (PlannedBeltRecord[])(r.Plans ?? Array.Empty<PlannedBeltRecord>()).Clone())
            {
                if (p?.Xs == null)
                {
                    continue;
                }
                int cancelled = 0;
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (p.CellState[i] == 0 && Inside(BlockOf(p.Xs[i]), BlockOf(p.Ys[i])))
                    {
                        p.CellState[i] = 2;
                        cancelled++;
                    }
                }
                if (cancelled == 0)
                {
                    continue;
                }
                LastCancelled += cancelled;
                string target = HomeValleyConstruction.RoofPlanPrefix + p.PlanId;
                WorkOrderRecord order = HomeValleyWorkOrders.FindActiveBuild(s, target);
                if (HomeValleyConstruction.UnbuiltCells(p) == 0)
                {
                    if (order != null)
                    {
                        HomeValleyWorkOrders.CancelOrder(s, order.WorkOrderId, HomeValleyConstruction.SitePosition(s, order));
                    }
                    else
                    {
                        RemovePlan(s, p);
                    }
                    continue;
                }
                int need = HomeValleyConstruction.UnbuiltCells(p) * p.ScrapPerCell;
                if (p.Delivered > need)
                {
                    int excess = p.Delivered - need;
                    p.Delivered = need;
                    HomeValleyConstruction.ReturnMaterials(s, new Vector2(p.Xs[0], p.Ys[0]), CampaignEconomyLedger.ResourceScrap, excess, target + ":trim");
                }
            }
            if (LastRemoved + LastCancelled > 0)
            {
                HomeValleyConstruction.Touch();
                Revision++;
                LastMessage = LastRemoved > 0 && LastCancelled > 0
                    ? GameText.Format("env.roof.removed", LastRemoved, LastRemoved * scrap) + GameText.Get("env.weather.intel_join") + GameText.Format("env.roof.cancelled", LastCancelled)
                    : LastRemoved > 0 ? GameText.Format("env.roof.removed", LastRemoved, LastRemoved * scrap) : GameText.Format("env.roof.cancelled", LastCancelled);
            }
            return LastRemoved + LastCancelled;
        }

        /// <summary>
        /// FG7-ENV-03 审查修复（P1）：拆除框 [a, b] 会拆掉 / 取消多少块顶棚（与 <see cref="DemolishBox"/> 同一口径：完整包住的已建成块 + 完整包住的施工中块），只读、不改存档。
        /// 拖框预览与“先确认再执行”用（顶棚和建筑 / 传送带在同一时机执行，确认框取消时什么都不拆）。O(顶棚块数 + 规划块数)，只在拖框换格 / 松手时调用。
        /// </summary>
        public static int CountDemolishBox(CampaignState s, GridCell a, GridCell b)
        {
            RoofState r = StateOf(s);
            if (r == null)
            {
                return 0;
            }
            int n = BlockCells;
            int minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            bool Inside(int bx, int by) => bx * n >= minX && bx * n + n - 1 <= maxX && by * n >= minY && by * n + n - 1 <= maxY;
            int count = 0;
            foreach (RoofRecord blk in r.Built ?? Array.Empty<RoofRecord>())
            {
                if (blk != null && Inside(blk.Bx, blk.By))
                {
                    count++;
                }
            }
            foreach (PlannedBeltRecord p in r.Plans ?? Array.Empty<PlannedBeltRecord>())
            {
                if (p?.Xs == null)
                {
                    continue;
                }
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (p.CellState[i] == 0 && Inside(BlockOf(p.Xs[i]), BlockOf(p.Ys[i])))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        // ── 隐藏（FGR-ENV-030“可以用快捷键隐藏顶棚”）──

        public static bool Hidden(CampaignState s) => s?.Weather?.Roofs?.Hidden ?? false;

        /// <summary>隐藏 / 显示顶棚（跟着存档走）。返回切换后的“隐藏”状态。</summary>
        public static bool ToggleHidden(CampaignState s)
        {
            RoofState r = StateOf(s);
            if (r == null)
            {
                return false;
            }
            r.Hidden = !r.Hidden;
            LastMessage = GameText.Format(r.Hidden ? "env.roof.toggle_hidden" : "env.roof.toggle_shown", InputDisplay.ForAction(GameActionId.ToggleRoofs));
            Revision++;
            return r.Hidden;
        }

        // ── 悬停 ──

        /// <summary>指着的格子有顶棚时的一行说明（建造模式状态行 / 世界悬停）；没有返回 null。</summary>
        public static string HoverText(CampaignState s, GridCell cell)
        {
            int bx = BlockOf(cell.X), by = BlockOf(cell.Y);
            bool built = HasBlock(s, bx, by);
            if (!built && !IsPlannedBlock(s, bx, by))
            {
                return null;
            }
            return GameText.Format("env.roof.hover", GameText.Get(built ? "env.roof.state.built" : "env.roof.state.planned"), InputDisplay.ForAction(GameActionId.ToggleRoofs));
        }

        // ── 传送带覆盖（派生量：读档 / 传送带内核重建后重新登记）──

        private static BeltKernel _coverKernel;
        private static RoofRecord[] _coverRef;

        private static void SetBlockCover(int bx, int by, bool covered)
        {
            if (!BeltNetworkService.IsRunning)
            {
                return;
            }
            int n = BlockCells;
            for (int dy = 0; dy < n; dy++)
            {
                for (int dx = 0; dx < n; dx++)
                {
                    BeltNetworkService.SetCovered(new GridCell(bx * n + dx, by * n + dy), covered);
                }
            }
        }

        /// <summary>
        /// 每步开头（<see cref="WeatherService.PreStep"/>）：传送带内核换了实例（读档 / 进出家园）或顶棚表换了引用时，按存档重新登记全部覆盖格。平时 O(1)。
        /// 顶棚建成 / 拆除时已当场登记（<see cref="CommitBlock"/> / <see cref="DemolishBox"/>）。
        /// </summary>
        public static void SyncBeltCover(CampaignState s)
        {
            if (!BeltNetworkService.IsRunning || !ReferenceEquals(BeltNetworkService.BoundState, s))
            {
                return;
            }
            RoofState r = s?.Weather?.Roofs;
            BeltKernel k = BeltNetworkService.Kernel;
            if (ReferenceEquals(k, _coverKernel) && ReferenceEquals(r?.Built, _coverRef))
            {
                return;
            }
            if (!ReferenceEquals(k, _coverKernel))
            {
                foreach (RoofRecord blk in r?.Built ?? Array.Empty<RoofRecord>())
                {
                    if (blk != null)
                    {
                        SetBlockCover(blk.Bx, blk.By, true);
                    }
                }
            }
            _coverKernel = k;
            _coverRef = r?.Built;
        }

        /// <summary>自检之间复位（不动存档）。</summary>
        public static void ResetForTests()
        {
            _indexed = null;
            _builtRef = null;
            _plansRef = null;
            _coverKernel = null;
            _coverRef = null;
            Built.Clear();
            Planned.Clear();
            LastPlaced = LastRemoved = LastCancelled = 0;
            LastMessage = string.Empty;
            Revision++;
        }
    }
}
