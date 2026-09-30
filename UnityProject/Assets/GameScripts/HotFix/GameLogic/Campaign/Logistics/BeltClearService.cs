using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using Unity.Mathematics;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>一次清带的规划（选中哪些格、上面有什么、仓库放得下多少）。</summary>
    public sealed class BeltClearPlan
    {
        /// <summary>选中的传送带格（只含已建成的格）。</summary>
        public readonly List<int2> Cells = new List<int2>(64);
        /// <summary>这些格上的物品，按种类。</summary>
        public readonly SortedDictionary<ushort, int> Counts = new SortedDictionary<ushort, int>();
        /// <summary>放不下（仓库满 / 家园仓库还存不了这种物品）的部分，按种类。</summary>
        public readonly SortedDictionary<ushort, int> Overflow = new SortedDictionary<ushort, int>();
        public int Items;
        /// <summary>能送进仓库的件数。</summary>
        public int Fit;
        public int OverflowItems;
        /// <summary>规划时家园仓库的剩余空间（废料）。</summary>
        public int FreeSpace;
        /// <summary>点选（整条带 = 所在网络）还是框选。</summary>
        public bool WholeNetwork;
        public GridCell Min;
        public GridCell Max;

        public bool HasBelts => Cells.Count > 0;
        public bool NeedsConfirm => OverflowItems > 0;

        public void Reset()
        {
            Cells.Clear();
            Counts.Clear();
            Overflow.Clear();
            Items = 0;
            Fit = 0;
            OverflowItems = 0;
            FreeSpace = 0;
            WholeNetwork = false;
        }
    }

    /// <summary>
    /// FG3-LOG-03（FG03 FGR-LOG-026“清带工具：清空选中传送带上的物品，送到最近的仓库；仓库放不下时询问玩家是否丢弃，并需要确认”）。
    /// - 选中：点一格 = 这条带（这一格所在的物流网络，含环）；拖框 = 框里已建成的传送带格（框边长受 grid.drag_max_cells 限制）。
    /// - 送到仓库：家园仓库（归还核心应急缓存 + 仓库，共用库存）放得下的废料直接入库（与拆除返还同一口径：视为机器顺手搬回）；
    ///   放不下的废料、以及家园仓库还存不了的物品（FG4-ECO-01 物品表之前）——算“放不下”，必须经玩家在确认框里同意才丢弃；
    ///   不同意则什么都不变（传送带和物品原样）。
    /// - 逐格 / 逐物品的循环在内核（<see cref="BeltKernel.CountItems"/> / <see cref="BeltKernel.ClearCells"/>）；这里只按种类汇总。只在玩家操作时发生。
    /// </summary>
    public static class BeltClearService
    {
        private static readonly Dictionary<ushort, int> CountScratch = new Dictionary<ushort, int>();

        /// <summary>最近一次清带的结果（状态行、自检）。</summary>
        public static int LastClearedCells { get; private set; }
        public static int LastStored { get; private set; }
        public static int LastDiscarded { get; private set; }

        /// <summary>点选：<paramref name="cell"/> 所在的整条带（物流网络）。这一格没有已建成的传送带时规划为空。</summary>
        public static BeltClearPlan PlanNetwork(CampaignState state, GridCell cell, BeltClearPlan into = null)
        {
            BeltClearPlan plan = into ?? new BeltClearPlan();
            plan.Reset();
            plan.WholeNetwork = true;
            plan.Min = cell;
            plan.Max = cell;
            if (!BeltNetworkService.IsRunning || !ReferenceEquals(state, BeltNetworkService.BoundState))
            {
                return plan;
            }
            BeltKernel k = BeltNetworkService.Kernel;
            int net = k.NetworkOf(cell.X, cell.Y);
            if (net < 0)
            {
                return plan;
            }
            k.CollectNetworkCells(net, plan.Cells);
            Fill(state, plan);
            return plan;
        }

        /// <summary>框选：框（闭区间，任意两角）里已建成的传送带格。框边长超过 grid.drag_max_cells 时按上限截断（与框选拆除同一上限）。</summary>
        public static BeltClearPlan PlanBox(CampaignState state, GridCell a, GridCell b, BeltClearPlan into = null)
        {
            BeltClearPlan plan = into ?? new BeltClearPlan();
            plan.Reset();
            int max = Math.Max(1, GridContent.TuningInt("grid.drag_max_cells"));
            int minX = Math.Min(a.X, b.X);
            int minY = Math.Min(a.Y, b.Y);
            int maxX = Math.Min(Math.Max(a.X, b.X), minX + max - 1);
            int maxY = Math.Min(Math.Max(a.Y, b.Y), minY + max - 1);
            plan.Min = new GridCell(minX, minY);
            plan.Max = new GridCell(maxX, maxY);
            if (!BeltNetworkService.IsRunning || !ReferenceEquals(state, BeltNetworkService.BoundState))
            {
                return plan;
            }
            // 框里的格由内核收集（逐格循环在 AOT，热更层不逐格）。
            BeltNetworkService.Kernel.CollectCellsInBox(minX, minY, maxX, maxY, plan.Cells);
            Fill(state, plan);
            return plan;
        }

        /// <summary>按种类数物品，并算出仓库放得下多少（废料按剩余空间；家园仓库存不了的种类全部“放不下”）。</summary>
        private static void Fill(CampaignState state, BeltClearPlan plan)
        {
            CountScratch.Clear();
            plan.Items = BeltNetworkService.Kernel.CountItems(plan.Cells, CountScratch);
            foreach (KeyValuePair<ushort, int> kv in CountScratch)
            {
                plan.Counts[kv.Key] = kv.Value;
            }
            plan.FreeSpace = HomeValleyCargo.GetAvailableSpace(state, CampaignEconomyLedger.ResourceScrap);
            int free = plan.FreeSpace;
            foreach (KeyValuePair<ushort, int> kv in plan.Counts)
            {
                int fit = BeltItems.IsStorable(kv.Key) ? Math.Min(kv.Value, free) : 0;
                free -= fit;
                plan.Fit += fit;
                if (kv.Value > fit)
                {
                    plan.Overflow[kv.Key] = kv.Value - fit;
                    plan.OverflowItems += kv.Value - fit;
                }
            }
        }

        /// <summary>
        /// 执行清带。执行时按当前状态重新数一遍（确认框开着的时候带还在走、仓库也可能变了）：放得下的送进仓库；
        /// 有放不下的而 <paramref name="discardOverflow"/> = false 时拒绝、什么都不变（返回 false）；= true（玩家在确认框里同意）时丢弃放不下的部分。
        /// </summary>
        public static bool Execute(CampaignState state, BeltClearPlan plan, bool discardOverflow, out string reasonKey)
        {
            reasonKey = null;
            LastClearedCells = 0;
            LastStored = 0;
            LastDiscarded = 0;
            if (state == null || plan == null || !plan.HasBelts)
            {
                reasonKey = "ui.build.clear_no_belt";
                return false;
            }
            if (!BeltNetworkService.IsRunning || !ReferenceEquals(state, BeltNetworkService.BoundState))
            {
                reasonKey = "logistics.reason.not_running";
                return false;
            }
            if (BeltNetworkService.SavedDataPreserved)
            {
                reasonKey = "logistics.reason.save_preserved";
                return false;
            }
            // 重新数（保留选中的格）。
            var cells = new List<int2>(plan.Cells);
            plan.Reset();
            plan.Cells.AddRange(cells);
            Fill(state, plan);
            if (plan.Items == 0)
            {
                reasonKey = "ui.build.clear_nothing";
                return false;
            }
            if (plan.NeedsConfirm && !discardOverflow)
            {
                reasonKey = "ui.build.clear_cancelled";
                return false;
            }
            CountScratch.Clear();
            int cleared = BeltNetworkService.Kernel.ClearCells(plan.Cells, CountScratch);
            int stored = 0;
            int discarded = 0;
            int free = Math.Max(0, HomeValleyCargo.GetAvailableSpace(state, CampaignEconomyLedger.ResourceScrap));
            var sorted = new List<KeyValuePair<ushort, int>>(CountScratch);
            sorted.Sort((x, y) => x.Key.CompareTo(y.Key));
            foreach (KeyValuePair<ushort, int> kv in sorted)
            {
                int fit = BeltItems.IsStorable(kv.Key) ? Math.Min(kv.Value, free) : 0;
                free -= fit;
                stored += fit;
                discarded += kv.Value - fit;
            }
            state.Scrap += stored; // 目前只有废料可存（BeltItems.IsStorable）：送进家园仓库（核心缓存与仓库共用库存）。
            state.Belts.ClearedToStorage += stored;
            state.Belts.Discarded += discarded;
            LastClearedCells = plan.Cells.Count;
            LastStored = stored;
            LastDiscarded = discarded;
            GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstClear);
            return cleared > 0;
        }
    }
}
