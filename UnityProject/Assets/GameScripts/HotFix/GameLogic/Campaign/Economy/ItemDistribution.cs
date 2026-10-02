using System;
using System.Collections.Generic;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>一种物品此刻在家园各处的数量（悬停“总库存 / 分布”的数据）。</summary>
    public sealed class ItemDistributionView
    {
        public ItemDef Item;
        /// <summary>各处合计（库存 + 在途 + 地面 + 货舱 / 管线里的总量 / 实体数）。</summary>
        public long Total;
        /// <summary>其中算“库存”的部分（仓库 + 核心缓存 + 保管库 + 数字账户），即可以直接取用的量。</summary>
        public long Stock;
        /// <summary>库存容量（固体 / 保管库；其它形态 0 = 不适用）。</summary>
        public int Capacity;
        /// <summary>分布：文本键 → 数量（只列非零的，顺序固定）。</summary>
        public readonly List<KeyValuePair<string, long>> Parts = new List<KeyValuePair<string, long>>(6);
    }

    /// <summary>
    /// FG4-ECO-01（FG04 第 4 节“悬停物品图标显示：总库存、各仓库分布、当前净速率”；FG-GAP-090“库存读数显示总库存 / 分布（含在途）”）：物品分布的缓存聚合。
    /// 来源：家园库存（核心缓存 / 仓库 / 保管库 / 数字账户，<see cref="HomeInventory"/>）、传送带上（内核按种类计数，在 AOT）、建筑端口缓存、地面待搬运、
    /// 机器货舱、管线与储罐（每个管线网络的存量）、机器名册与固件库（实体）。在途的单列，不算库存。
    /// 缓存：一次重建全部物品；悬停 / 面板打开时取，按 eco.hover.refresh_seconds 真实秒定时重读（默认 0.5 秒）。
    /// 库存版本（<see cref="HomeInventory.Revision"/>）故意不进缓存键：传送带端口每个内核步都在入库 / 补货（20 Hz，3x 时 60 Hz），
    /// 跟着它失效等于每步重建。只有“换了存档 / 物品表重载 / 端口绑定增删改”（玩家显式操作或读档）与 <see cref="Invalidate"/>（开面板、读档）才提前重建。
    /// 开销：重建 O(传送带格 + 端口 + 地面物 + 机器 + 管线网络 + 物品种类)（按格计数在 AOT），只在有人悬停 / 面板打开时发生，
    /// 每 refresh_seconds 真实秒至多一次（默认每秒至多 2 次）；不悬停时零开销，不按帧。
    /// </summary>
    public static class ItemDistribution
    {
        /// <summary>传送带物品编号的计数数组长度（表里 beltId ≤ 999；超出的计入最后一格）。</summary>
        private const int BeltSlots = 1024;

        private static readonly Dictionary<string, ItemDistributionView> Views = new Dictionary<string, ItemDistributionView>(StringComparer.Ordinal);
        private static readonly int[] BeltCounts = new int[BeltSlots];
        private static readonly int[] PortCounts = new int[BeltSlots];
        private static readonly Dictionary<string, long> Ground = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> Cargo = new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>FG4-ECO-02：生产建筑缓存里的物品（件 / 升）。</summary>
        private static readonly Dictionary<string, long> Production = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<int, long> PipeLiters = new Dictionary<int, long>();
        /// <summary>FG5-RND-02（DEBT-FG4ECO01-04）：解析台里的敌方物品（队列在办 / 残骸缓存）与 Demo 区域任务物（已带回待解析）。</summary>
        private static readonly Dictionary<string, long> InBench = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> Quest = new Dictionary<string, long>(StringComparer.Ordinal);
        private static double _builtAt = double.NegativeInfinity;
        private static int _key;

        /// <summary>真实时间来源（自检替换）。</summary>
        public static Func<double> RealTime = () => Time.realtimeSinceStartupAsDouble;

        public static int BuildCount { get; private set; }
        public static double LastBuildMs { get; private set; }
        public static double MaxBuildMs { get; private set; }

        public static float RefreshSeconds => Mathf.Max(0.05f, Grid.GridContent.Tuning("eco.hover.refresh_seconds"));

        public static void Invalidate() => _builtAt = double.NegativeInfinity;

        public static void ResetForTests()
        {
            Invalidate();
            Views.Clear();
            BuildCount = 0;
            LastBuildMs = 0;
            MaxBuildMs = 0;
            RealTime = () => Time.realtimeSinceStartupAsDouble;
        }

        public static ItemDistributionView Get(CampaignState state, ItemDef item)
        {
            if (item == null)
            {
                return null;
            }
            EnsureFresh(state);
            return Views.TryGetValue(item.Id, out ItemDistributionView v) ? v : new ItemDistributionView { Item = item };
        }

        private static void EnsureFresh(CampaignState state)
        {
            // 不含 HomeInventory.Revision（见类注释：端口每个内核步都会改库存，跟着失效就成了每步重建）。
            int key = HashCode.Combine(state != null ? state.GetHashCode() : 0, ItemCatalog.Revision, BeltPortService.Revision);
            double now = RealTime();
            if (key == _key && now - _builtAt < RefreshSeconds && Views.Count > 0)
            {
                return;
            }
            _key = key;
            _builtAt = now;
            Rebuild(state);
        }

        private static void Rebuild(CampaignState state)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Views.Clear();
            Array.Clear(BeltCounts, 0, BeltCounts.Length);
            Array.Clear(PortCounts, 0, PortCounts.Length);
            Ground.Clear();
            Cargo.Clear();
            PipeLiters.Clear();
            Production.Clear();
            InBench.Clear();
            Quest.Clear();
            HomeValleyAnalysis.CollectDistribution(state, InBench, Quest);
            // FG4-ECO-02：生产建筑自己的输入 / 输出缓存与流体口里的流体（在途，不算库存）。
            ProductionService.CollectBuffers(state, Production);
            if (state != null && BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, state))
            {
                BeltNetworkService.Kernel.CountItemsByType(-1, BeltCounts);
                BeltPortService.CollectPortItems(state, PortCounts);
            }
            if (state?.GroundItems != null)
            {
                foreach (GroundItemRecord g in state.GroundItems)
                {
                    if (g != null && g.Amount > 0 && g.RegionId == HomeValleyLayout.RegionId && ItemCatalog.TryGetByResource(g.ResourceType, out ItemDef d))
                    {
                        Ground[d.Id] = (Ground.TryGetValue(d.Id, out long n) ? n : 0) + g.Amount;
                    }
                }
            }
            int machines = 0;
            if (state?.MachineRecords != null)
            {
                foreach (MachineRecord m in state.MachineRecords)
                {
                    if (m == null || !m.IsAlive)
                    {
                        continue;
                    }
                    machines++;
                    if (m.Cargo == null)
                    {
                        continue;
                    }
                    foreach (CargoEntry c in m.Cargo)
                    {
                        if (c != null && c.Amount > 0 && ItemCatalog.TryGetByResource(c.ResourceType, out ItemDef d))
                        {
                            Cargo[d.Id] = (Cargo.TryGetValue(d.Id, out long n) ? n : 0) + c.Amount;
                        }
                    }
                }
            }
            if (state != null && PipeNetworkService.IsRunning && ReferenceEquals(PipeNetworkService.BoundState, state))
            {
                BinGames.Sim.Logistics.PipeKernel pk = PipeNetworkService.Kernel;
                int nets = pk.NetworkCount;
                for (int net = 0; net < nets; net++)
                {
                    if (pk.TryGetNetworkInfo(net, out BinGames.Sim.Logistics.PipeNetInfo info) && info.Fluid > 0 && info.StoredMl > 0)
                    {
                        PipeLiters[info.Fluid] = (PipeLiters.TryGetValue(info.Fluid, out long n) ? n : 0) + info.StoredMl / 1000;
                    }
                }
            }
            bool warehouse = HomeInventory.WarehouseOperational(state);
            int coreCache = HomeValleyLayout.CoreCacheCapacity;
            foreach (ItemDef item in ItemCatalog.Items)
            {
                var v = new ItemDistributionView { Item = item };
                switch (item.Form)
                {
                    case ItemForm.Solid:
                    {
                        int stock = HomeInventory.Stock(state, item);
                        v.Stock = stock;
                        v.Capacity = HomeInventory.Capacity(state, item, warehouse);
                        if (item.Id == ItemCatalog.ScrapId)
                        {
                            // 没有运转中的仓库时全部算在核心缓存里（测试捷径 / 旧档可能超出容量，不把多出来的写成“仓库”）。
                            int inCore = warehouse ? Math.Min(stock, coreCache) : stock;
                            Add(v, "item.dist.core_cache", inCore);
                            Add(v, "item.dist.warehouse", stock - inCore);
                        }
                        else
                        {
                            Add(v, "item.dist.warehouse", stock);
                        }
                        if (item.BeltId != 0)
                        {
                            int slot = item.BeltId < BeltSlots - 1 ? item.BeltId : BeltSlots - 1;
                            Add(v, "item.dist.belts", BeltCounts[slot]);
                            Add(v, "item.dist.ports", PortCounts[slot]);
                        }
                        break;
                    }
                    case ItemForm.Vault:
                        v.Stock = HomeInventory.Stock(state, item);
                        v.Capacity = HomeInventory.Capacity(state, item, warehouse);
                        Add(v, "item.dist.vault", v.Stock);
                        break;
                    case ItemForm.Digital:
                        v.Stock = HomeInventory.Stock(state, item);
                        Add(v, "item.dist.digital", v.Stock);
                        break;
                    case ItemForm.Fluid:
                        Add(v, "item.dist.pipes", PipeLiters.TryGetValue(item.FluidId, out long liters) ? liters : 0);
                        break;
                    case ItemForm.Entity:
                        if (item.Id == ItemCatalog.MachineId)
                        {
                            Add(v, "item.dist.roster", machines);
                        }
                        else if (item.Id == ItemCatalog.FirmwareChipId)
                        {
                            Add(v, "item.dist.library", PrimitiveInventory.BagCount(state));
                        }
                        break;
                }
                Add(v, "item.dist.production", Production.TryGetValue(item.Id, out long pb) ? pb : 0);
                Add(v, "item.dist.ground", Ground.TryGetValue(item.Id, out long g) ? g : 0);
                Add(v, "item.dist.cargo", Cargo.TryGetValue(item.Id, out long c) ? c : 0);
                Add(v, "item.dist.analysis", InBench.TryGetValue(item.Id, out long ab) ? ab : 0);
                Add(v, "item.dist.quest", Quest.TryGetValue(item.Id, out long qi) ? qi : 0);
                Views[item.Id] = v;
            }
            BuildCount++;
            LastBuildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MaxBuildMs = Math.Max(MaxBuildMs, LastBuildMs);
        }

        private static void Add(ItemDistributionView v, string key, long amount)
        {
            if (amount <= 0)
            {
                return;
            }
            v.Parts.Add(new KeyValuePair<string, long>(key, amount));
            v.Total += amount;
        }
    }
}
