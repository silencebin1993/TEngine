using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-01（FG04 第 4 节“悬停物品图标显示……当前净速率”；第 6 节“统计按时间窗口聚合保存”；第 7 节“统计按时间窗口聚合，不能每帧遍历所有建筑”）：物品净速率。
    /// - 每 eco.flow.sample_seconds 游戏秒（按世界步序号，与镜头 / 帧率 / 倍速 / 是否观察家园无关）把每种家园库存物品（固体、保管库、数字资源）的库存记一条采样，
    ///   保留 eco.flow.window_samples + 1 条；净速率 = 窗口首尾库存差 ÷ 游戏分钟。暂停时世界不走步，不采样；3x 下同一段游戏时间采到同样的样。
    /// - 采样进存档（<see cref="EconomyState.FlowSamples"/>）：读档后净速率接着算，与不存档一致。物品表变了（列对不上）时整体重采。
    /// - FG4-ECO-08 在此基础上扩四个时间窗口、产量 / 消耗分开统计。
    /// 开销：每次采样 O(物品种类数)（约 30），每 10 游戏秒一次；查询 O(1)。
    /// </summary>
    public static class ItemFlowStats
    {
        public static int SampleSeconds => Math.Max(1, GridContent.TuningInt("eco.flow.sample_seconds"));
        public static int WindowSamples => Math.Max(1, GridContent.TuningInt("eco.flow.window_samples"));

        /// <summary>采样次数（自检读点）。</summary>
        public static int SampleCount { get; private set; }

        private static readonly List<ItemDef> Columns = new List<ItemDef>(32);
        private static int _columnsRevision = -1;
        private static string[] _columnIds = Array.Empty<string>();

        /// <summary>参与净速率的物品（家园库存里的：固体、保管库、数字资源；按表顺序）。</summary>
        public static IReadOnlyList<ItemDef> TrackedItems
        {
            get
            {
                EnsureColumns();
                return Columns;
            }
        }

        public static bool IsTracked(ItemDef item) =>
            item != null && (item.Form == ItemForm.Solid || item.Form == ItemForm.Vault || item.Form == ItemForm.Digital);

        /// <summary>世界步末调用（家园载入时）：到了采样的步就采一次。</summary>
        public static void WorldStep(CampaignState state, long ticks, int stepHz)
        {
            if (state == null)
            {
                return;
            }
            long interval = Math.Max(1L, (long)SampleSeconds * Math.Max(1, stepHz));
            if (ticks % interval != 0)
            {
                return;
            }
            Sample(state, ticks);
        }

        /// <summary>立即采一条（世界步与自检调用）。</summary>
        public static void Sample(CampaignState state, long tick)
        {
            if (state.Economy?.FlowSamples == null || state.Economy.FlowItemIds == null)
            {
                CampaignFgStateDomains.EnsureAll(state); // 旧存档没有物资域：补空域（只在缺的时候走一遍）
            }
            EnsureColumns();
            EconomyState e = state.Economy;
            if (!SameColumns(e.FlowItemIds))
            {
                e.FlowItemIds = (string[])_columnIds.Clone();
                e.FlowSamples = Array.Empty<ItemFlowSampleRecord>();
            }
            ItemFlowSampleRecord[] old = e.FlowSamples;
            if (old.Length > 0 && old[old.Length - 1] != null && old[old.Length - 1].Tick == tick)
            {
                return; // 同一步重复调用（读档后第一步等）：不重复记
            }
            var stocks = new int[Columns.Count];
            for (int i = 0; i < Columns.Count; i++)
            {
                stocks[i] = HomeInventory.Stock(state, Columns[i]);
            }
            int keep = WindowSamples + 1;
            int from = Math.Max(0, old.Length + 1 - keep);
            var next = new ItemFlowSampleRecord[old.Length - from + 1];
            Array.Copy(old, from, next, 0, old.Length - from);
            next[next.Length - 1] = new ItemFlowSampleRecord { Tick = tick, Stocks = stocks };
            e.FlowSamples = next;
            SampleCount++;
        }

        /// <summary>
        /// 净速率（件 / 游戏分钟）。窗口还没攒够两条采样时返回 false，<paramref name="windowSeconds"/> 给出已有的窗口长度、
        /// <paramref name="waitSeconds"/> 给出还要等多久（界面写“测量中”）。
        /// </summary>
        public static bool TryNetPerMinute(CampaignState state, ItemDef item, int stepHz, out float perMinute, out float windowSeconds, out float waitSeconds)
        {
            perMinute = 0f;
            windowSeconds = 0f;
            waitSeconds = SampleSeconds;
            if (state?.Economy?.FlowSamples == null || !IsTracked(item))
            {
                return false;
            }
            EnsureColumns();
            ItemFlowSampleRecord[] s = state.Economy.FlowSamples;
            int col = SameColumns(state.Economy.FlowItemIds) ? IndexOf(item.Id) : -1;
            if (col < 0 || s.Length < 2 || s[0] == null || s[s.Length - 1] == null)
            {
                return false;
            }
            ItemFlowSampleRecord a = s[0];
            ItemFlowSampleRecord b = s[s.Length - 1];
            if (col >= a.Stocks.Length || col >= b.Stocks.Length || b.Tick <= a.Tick)
            {
                return false;
            }
            windowSeconds = (float)((b.Tick - a.Tick) / (double)Math.Max(1, stepHz));
            perMinute = (b.Stocks[col] - a.Stocks[col]) * 60f / Math.Max(0.001f, windowSeconds);
            waitSeconds = 0f;
            return true;
        }

        private static int IndexOf(string id)
        {
            for (int i = 0; i < _columnIds.Length; i++)
            {
                if (_columnIds[i] == id)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool SameColumns(string[] ids)
        {
            if (ids == null || ids.Length != _columnIds.Length)
            {
                return false;
            }
            for (int i = 0; i < ids.Length; i++)
            {
                if (!string.Equals(ids[i], _columnIds[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static void EnsureColumns()
        {
            if (_columnsRevision == ItemCatalog.Revision)
            {
                return;
            }
            Columns.Clear();
            foreach (ItemDef d in ItemCatalog.Items)
            {
                if (IsTracked(d))
                {
                    Columns.Add(d);
                }
            }
            _columnIds = new string[Columns.Count];
            for (int i = 0; i < Columns.Count; i++)
            {
                _columnIds[i] = Columns[i].Id;
            }
            _columnsRevision = ItemCatalog.Revision;
        }

        public static void ResetSessionState()
        {
            SampleCount = 0;
        }
    }
}
