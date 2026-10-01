using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-01（FG04 FGR-ECO-001“关键材料和人类遗产存放在归还核心的核心保管库，不上传送带”；继承 DEBT-FG3LOG03-02 / FG3LOG02-01“家园仓库只存废料”）：
    /// 家园物资的唯一读写入口。
    /// - 固体：废料仍是 <see cref="CampaignState.Scrap"/>（Demo 起的唯一真相，核心缓存 + 仓库共用，容量 = 核心缓存 + 运转中的仓库）；
    ///   其余固体存在 <see cref="EconomyState.Items"/>，每种各自的容量 = 运转中的仓库容量（核心缓存只收废料，ERD-ECO-003）。
    /// - 核心保管库（form = vault）：<see cref="EconomyState.Vault"/>，每种容量 eco.vault.capacity，不上传送带。
    /// - 数字资源：技术数据 = <see cref="CampaignState.TechData"/>，研究点 = <see cref="ResearchState.Points"/>（FG5-RND-01 写入）；不占物理空间。
    /// - 流体在管线与储罐里（<see cref="ItemDistribution"/> 读），机器与固件芯片是实体（名册 / 固件库），都不经本类存取。
    /// 开销：查询与增减 O(1)（按物品 ID 的索引随存档数组引用重建，新物品种类出现时 O(种类数) 插入一次）；传送带端口每个内核步按端口调用。
    /// </summary>
    public static class HomeInventory
    {
        /// <summary>任何一次增减 +1（悬停缓存 / 面板据此失效）。</summary>
        public static int Revision { get; private set; } = 1;

        private static CampaignState _state;
        private static ItemStackRecord[] _indexedItems;
        private static ItemStackRecord[] _indexedVault;
        private static readonly Dictionary<string, ItemStackRecord> ItemIndex = new Dictionary<string, ItemStackRecord>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ItemStackRecord> VaultIndex = new Dictionary<string, ItemStackRecord>(StringComparer.Ordinal);

        public static void Touch() => Revision++;

        public static bool IsStorable(ItemDef item) => item != null && (item.Form == ItemForm.Solid || item.Form == ItemForm.Vault);

        /// <summary>能被家园仓库收、能上传送带（固体且有传送带编号）。</summary>
        public static bool IsBeltStorable(ItemDef item) => item != null && item.Form == ItemForm.Solid && item.BeltId != 0;

        /// <summary>家园里有没有运转中的仓库（容量来源）。O(建筑数)：调用方在一次批量操作里只查一次。</summary>
        public static bool WarehouseOperational(CampaignState state)
        {
            BuildingRecord[] records = state?.BuildingRecords;
            if (records == null)
            {
                return false;
            }
            foreach (BuildingRecord b in records)
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse
                    && b.ConstructionState == BuildingConstructionState.Operational)
                {
                    return true;
                }
            }
            return false;
        }

        public static int VaultCapacity => Math.Max(1, GridContent.TuningInt("eco.vault.capacity"));

        // ── 查询 ─────────────────────────────────────────────────────────────────

        public static int Stock(CampaignState state, string itemId) => ItemCatalog.TryGet(itemId, out ItemDef d) ? Stock(state, d) : 0;

        /// <summary>家园库存（仓库 + 核心缓存 / 保管库 / 数字账户）。流体与实体不在这里，返回 0（总量见 <see cref="ItemDistribution"/>）。</summary>
        public static int Stock(CampaignState state, ItemDef item)
        {
            if (state == null || item == null)
            {
                return 0;
            }
            switch (item.Form)
            {
                case ItemForm.Solid:
                    if (item.Id == ItemCatalog.ScrapId)
                    {
                        return Math.Max(0, state.Scrap);
                    }
                    return Find(state, item.Id, vault: false)?.Amount ?? 0;
                case ItemForm.Vault:
                    return Find(state, item.Id, vault: true)?.Amount ?? 0;
                case ItemForm.Digital:
                    if (item.Id == ItemCatalog.TechDataId)
                    {
                        return Math.Max(0, state.TechData);
                    }
                    if (item.Id == ItemCatalog.ResearchPointsId)
                    {
                        return Math.Max(0, state.Research?.Points ?? 0);
                    }
                    return 0;
                default:
                    return 0;
            }
        }

        public static int Capacity(CampaignState state, ItemDef item) => Capacity(state, item, WarehouseOperational(state));

        /// <summary>这种物品的家园容量。废料 = 核心缓存 + 运转中的仓库；其余固体 = 运转中的仓库；保管库 = eco.vault.capacity；数字资源不限。</summary>
        public static int Capacity(CampaignState state, ItemDef item, bool warehouseOperational)
        {
            if (item == null)
            {
                return 0;
            }
            switch (item.Form)
            {
                case ItemForm.Solid:
                    int warehouse = warehouseOperational ? HomeValleyLayout.WarehouseCapacity : 0;
                    return item.Id == ItemCatalog.ScrapId ? HomeValleyLayout.CoreCacheCapacity + warehouse : warehouse;
                case ItemForm.Vault:
                    return VaultCapacity;
                case ItemForm.Digital:
                    return int.MaxValue;
                default:
                    return 0;
            }
        }

        public static int Space(CampaignState state, ItemDef item) => Space(state, item, WarehouseOperational(state));

        public static int Space(CampaignState state, ItemDef item, bool warehouseOperational) =>
            Math.Max(0, Capacity(state, item, warehouseOperational) - Stock(state, item));

        // ── 增减（唯一写入口）─────────────────────────────────────────────────────

        /// <summary>
        /// 存进家园。<paramref name="clampToSpace"/> = true 时最多存到容量（返回实际存入数，调用方处理放不下的部分）；
        /// = false 时调用方已经查过容量（例如搬运交付前），照数存入。不能存的形态（流体 / 实体）返回 0。
        /// 远征物 / 关键材料 / 终局物品第一次进家园时解锁它的图鉴条目。
        /// </summary>
        public static int Add(CampaignState state, ItemDef item, int amount, bool clampToSpace = true, bool warehouseOperational = true, bool knownWarehouse = false)
        {
            if (state == null || item == null || amount <= 0)
            {
                return 0;
            }
            if (!IsStorable(item) && item.Form != ItemForm.Digital)
            {
                return 0;
            }
            int before = Stock(state, item);
            int n = amount;
            if (clampToSpace)
            {
                bool wh = knownWarehouse ? warehouseOperational : WarehouseOperational(state);
                n = Math.Min(amount, Math.Max(0, Capacity(state, item, wh) - before));
            }
            if (n <= 0)
            {
                return 0;
            }
            switch (item.Form)
            {
                case ItemForm.Solid when item.Id == ItemCatalog.ScrapId:
                    state.Scrap += n;
                    break;
                case ItemForm.Solid:
                    GetOrCreate(state, item.Id, vault: false).Amount += n;
                    break;
                case ItemForm.Vault:
                    GetOrCreate(state, item.Id, vault: true).Amount += n;
                    break;
                case ItemForm.Digital when item.Id == ItemCatalog.TechDataId:
                    state.TechData += n;
                    break;
                case ItemForm.Digital when item.Id == ItemCatalog.ResearchPointsId:
                    if (state.Research == null)
                    {
                        CampaignFgStateDomains.EnsureAll(state);
                    }
                    state.Research.Points += n;
                    break;
                default:
                    return 0;
            }
            Revision++;
            if (before <= 0 && !item.CodexAlwaysOpen)
            {
                Progression.MechanicCodex.Unlock(Progression.MechanicCodex.ItemEntryId(item.Id));
            }
            return n;
        }

        public static int Add(CampaignState state, string itemId, int amount, bool clampToSpace = true) =>
            ItemCatalog.TryGet(itemId, out ItemDef d) ? Add(state, d, amount, clampToSpace) : 0;

        /// <summary>从家园取走最多 <paramref name="amount"/> 件，返回实际取走数。</summary>
        public static int RemoveUpTo(CampaignState state, ItemDef item, int amount)
        {
            if (state == null || item == null || amount <= 0)
            {
                return 0;
            }
            int n = Math.Min(amount, Stock(state, item));
            if (n <= 0)
            {
                return 0;
            }
            switch (item.Form)
            {
                case ItemForm.Solid when item.Id == ItemCatalog.ScrapId:
                    state.Scrap -= n;
                    break;
                case ItemForm.Solid:
                    Find(state, item.Id, vault: false).Amount -= n;
                    break;
                case ItemForm.Vault:
                    Find(state, item.Id, vault: true).Amount -= n;
                    break;
                case ItemForm.Digital when item.Id == ItemCatalog.TechDataId:
                    state.TechData -= n;
                    break;
                case ItemForm.Digital when item.Id == ItemCatalog.ResearchPointsId:
                    state.Research.Points -= n;
                    break;
                default:
                    return 0;
            }
            Revision++;
            return n;
        }

        /// <summary>全有或全无地取走（不够就什么都不动，返回 false）。</summary>
        public static bool TryRemove(CampaignState state, ItemDef item, int amount)
        {
            if (amount <= 0)
            {
                return true;
            }
            if (Stock(state, item) < amount)
            {
                return false;
            }
            return RemoveUpTo(state, item, amount) == amount;
        }

        // ── 存储索引 ─────────────────────────────────────────────────────────────

        private static ItemStackRecord Find(CampaignState state, string itemId, bool vault)
        {
            EnsureIndex(state);
            return (vault ? VaultIndex : ItemIndex).TryGetValue(itemId, out ItemStackRecord r) ? r : null;
        }

        private static ItemStackRecord GetOrCreate(CampaignState state, string itemId, bool vault)
        {
            ItemStackRecord r = Find(state, itemId, vault);
            if (r != null)
            {
                return r;
            }
            if (state.Economy?.Items == null || state.Economy.Vault == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            ItemStackRecord[] old = vault ? state.Economy.Vault : state.Economy.Items;
            var next = new ItemStackRecord[old.Length + 1];
            int at = 0;
            while (at < old.Length && string.CompareOrdinal(old[at]?.ItemId, itemId) < 0)
            {
                at++;
            }
            Array.Copy(old, 0, next, 0, at);
            r = new ItemStackRecord { ItemId = itemId, Amount = 0 };
            next[at] = r;
            Array.Copy(old, at, next, at + 1, old.Length - at);
            if (vault)
            {
                state.Economy.Vault = next;
            }
            else
            {
                state.Economy.Items = next;
            }
            EnsureIndex(state);
            return r;
        }

        private static void EnsureIndex(CampaignState state)
        {
            ItemStackRecord[] items = state?.Economy?.Items;
            ItemStackRecord[] vault = state?.Economy?.Vault;
            if (ReferenceEquals(state, _state) && ReferenceEquals(items, _indexedItems) && ReferenceEquals(vault, _indexedVault))
            {
                return;
            }
            _state = state;
            _indexedItems = items;
            _indexedVault = vault;
            Fill(ItemIndex, items);
            Fill(VaultIndex, vault);
        }

        private static void Fill(Dictionary<string, ItemStackRecord> index, ItemStackRecord[] records)
        {
            index.Clear();
            if (records == null)
            {
                return;
            }
            foreach (ItemStackRecord r in records)
            {
                if (r != null && !string.IsNullOrEmpty(r.ItemId) && !index.ContainsKey(r.ItemId))
                {
                    index[r.ItemId] = r;
                }
            }
        }

        /// <summary>换战役 / 自检时清索引（下一次访问按新存档重建）。</summary>
        public static void ResetSessionState()
        {
            _state = null;
            _indexedItems = null;
            _indexedVault = null;
            ItemIndex.Clear();
            VaultIndex.Clear();
            Revision++;
        }
    }
}
