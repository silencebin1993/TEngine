using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameConfig.fg;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>建筑在废料之外要的一种材料（fg.TbBuildMaterial 一行的运行时视图）。</summary>
    public readonly struct BuildMaterialNeed
    {
        public readonly ItemDef Item;
        public readonly int Amount;

        public BuildMaterialNeed(ItemDef item, int amount)
        {
            Item = item;
            Amount = amount;
        }

        /// <summary>家园资源类型（施工现场、货舱、返还都用它）：物品 ID。</summary>
        public string ResourceType => Item?.ResourceType ?? string.Empty;
    }

    /// <summary>FG4-ECO-11：一批规划（粘贴、框选升级）里废料之外的材料合计（按物品累加，保持首次出现的顺序）。</summary>
    public sealed class BuildMaterialTally
    {
        private readonly List<BuildMaterialNeed> _items = new List<BuildMaterialNeed>(2);

        public IReadOnlyList<BuildMaterialNeed> Items => _items;
        public bool Any => _items.Count > 0;

        public void Clear() => _items.Clear();

        public void Add(IReadOnlyList<BuildMaterialNeed> list)
        {
            if (list == null)
            {
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                BuildMaterialNeed n = list[i];
                int at = _items.FindIndex(x => x.Item.Id == n.Item.Id);
                if (at >= 0)
                {
                    _items[at] = new BuildMaterialNeed(n.Item, _items[at].Amount + n.Amount);
                }
                else
                {
                    _items.Add(n);
                }
            }
        }

        /// <summary>“另需 监听阵列核 ×1（核心保管库 0）”，缺货时再逐条写从哪里获得；没有额外材料返回空串。</summary>
        public string Describe(CampaignState state)
        {
            if (_items.Count == 0)
            {
                return string.Empty;
            }
            string text = GameText.Format("build.cost.extra", BuildMaterials.DescribeList(state, _items));
            string shortfall = BuildMaterials.DescribeShortfall(state, _items);
            return shortfall != null ? text + "\n" + shortfall : text;
        }
    }

    /// <summary>
    /// FG4-ECO-11（承接 DEBT-FG3LOG02-02 多材料造价；FG04 FGR-ECO-020 超控阵列的关键材料）：建筑在废料之外要的材料（fg.TbBuildMaterial）。
    /// tier = 1 是新建（以及没有等级的建筑），tier = N（&gt;= 2）是原地升级到第 N 级的差额。材料只能是家园能存的物品（固体在仓库、关键材料在核心保管库）。
    ///
    /// 施工照旧走虚影：放置 / 升级不扣料，虚影记下所需（<see cref="BuildingRecord.ExtraMaterialIds"/>），机器从仓库 / 核心保管库逐种取来；
    /// 取消全额退回、完工记为投入、拆除全额退回、被摧毁时投入留在建筑里（重建只收废料）。本类只回答“要什么、现在有多少、缺了去哪儿拿”，
    /// 状态写入在 <see cref="Regions.HomeValleyConstruction"/> / <see cref="Regions.HomeValleyWorkOrders"/>。表很小（十几行），按需建索引，没有每帧开销。
    /// </summary>
    public static class BuildMaterials
    {
        private static bool _loaded;
        private static readonly Dictionary<string, List<BuildMaterialNeed>> ByKey = new Dictionary<string, List<BuildMaterialNeed>>(StringComparer.Ordinal);
        private static readonly List<string> _problems = new List<string>();
        private static readonly List<BuildMaterialNeed> Empty = new List<BuildMaterialNeed>(0);

        /// <summary>加载表时发现的问题（物品不存在 / 家园存不了 / 件数非法）：这些行被拒绝，自检读它。</summary>
        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return _problems;
            }
        }

        public static void Reload()
        {
            _loaded = false;
            ByKey.Clear();
            _problems.Clear();
        }

        public static void ResetForTests() => Reload();

        private static string Key(string typeId, int tier) => typeId + "#" + Math.Max(1, tier).ToString(CultureInfo.InvariantCulture);

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            TbBuildMaterial table = null;
            try
            {
                table = ConfigSystem.Instance.Tables?.TbBuildMaterial;
            }
            catch (Exception e)
            {
                _problems.Add("读取 fg.TbBuildMaterial 失败：" + e.Message);
                Log.Error("[BuildMaterials] 读取 fg.TbBuildMaterial 失败：" + e.Message);
            }
            if (table == null)
            {
                return;
            }
            foreach (BuildMaterial row in table.DataList)
            {
                if (!ItemCatalog.TryGet(row.ItemId, out ItemDef item))
                {
                    _problems.Add($"{row.Id}：物品 {row.ItemId} 不在物品表里");
                    continue;
                }
                if (!HomeInventory.IsStorable(item) || item.Id == ItemCatalog.ScrapId)
                {
                    _problems.Add($"{row.Id}：物品 {row.ItemId} 家园存不了或是废料（废料走 buildScrap / diffScrap）");
                    continue;
                }
                if (row.Amount < 1)
                {
                    _problems.Add($"{row.Id}：件数 {row.Amount} 至少 1");
                    continue;
                }
                string key = Key(row.TypeId, row.Tier);
                if (!ByKey.TryGetValue(key, out List<BuildMaterialNeed> list))
                {
                    ByKey[key] = list = new List<BuildMaterialNeed>(2);
                }
                list.Add(new BuildMaterialNeed(item, row.Amount));
            }
            foreach (string p in _problems)
            {
                Log.Error("[BuildMaterials] " + p);
            }
        }

        // ── 查询 ─────────────────────────────────────────────────────────────────

        /// <summary>这一类建筑第 <paramref name="tier"/> 级（1 = 新建）在废料之外要的材料；没有 = 空列表。</summary>
        public static IReadOnlyList<BuildMaterialNeed> For(string typeId, int tier)
        {
            EnsureLoaded();
            return typeId != null && ByKey.TryGetValue(Key(typeId, tier), out List<BuildMaterialNeed> l) ? l : Empty;
        }

        public static IReadOnlyList<BuildMaterialNeed> NewBuild(string typeId) => For(typeId, 1);

        /// <summary>原地升级到第 <paramref name="toTier"/> 级的差额材料（&lt; 2 = 没有等级的升级路线，空）。</summary>
        public static IReadOnlyList<BuildMaterialNeed> Upgrade(string typeId, int toTier) => toTier >= 2 ? For(typeId, toTier) : Empty;

        public static int Stock(CampaignState state, ItemDef item) => HomeInventory.Stock(state, item);

        /// <summary>“核心保管库” / “仓库”：这种材料放在哪儿。</summary>
        public static string StoreName(ItemDef item) =>
            GameText.Get(item != null && item.Form == ItemForm.Vault ? "build.cost.store.vault" : "build.cost.store.warehouse");

        /// <summary>“监听阵列核 ×1（核心保管库 0）、合金 ×12（仓库 40）”；空列表返回空串。</summary>
        public static string DescribeList(CampaignState state, IReadOnlyList<BuildMaterialNeed> list)
        {
            if (list == null || list.Count == 0)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(GameText.Get("build.cost.extra_sep"));
                }
                BuildMaterialNeed n = list[i];
                sb.Append(GameText.Format("build.cost.extra_item", n.Item.Name, n.Amount.ToString(CultureInfo.InvariantCulture), StoreName(n.Item),
                    Stock(state, n.Item).ToString(CultureInfo.InvariantCulture)));
            }
            return sb.ToString();
        }

        /// <summary>这些材料现在家园里都够吗。</summary>
        public static bool AllInStock(CampaignState state, IReadOnlyList<BuildMaterialNeed> list)
        {
            if (list == null)
            {
                return true;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (Stock(state, list[i].Item) < list[i].Amount)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>这种物品从哪儿来（物品表的“来源”文字，FG4-ECO-01；关键材料写明哪个首领给出）。</summary>
        public static string SourceOf(ItemDef item) =>
            item == null ? string.Empty : !string.IsNullOrEmpty(item.SourceKey) ? GameText.Get(item.SourceKey) : string.Empty;

        /// <summary>
        /// 缺的材料逐条“缺 监听阵列核 ×1：主线：击败静默的首领……”（卡片“关键材料缺失时，面板说明从哪里获得”，B06）；都够返回 null。
        /// </summary>
        public static string DescribeShortfall(CampaignState state, IReadOnlyList<BuildMaterialNeed> list)
        {
            if (list == null || list.Count == 0)
            {
                return null;
            }
            StringBuilder sb = null;
            for (int i = 0; i < list.Count; i++)
            {
                BuildMaterialNeed n = list[i];
                int miss = n.Amount - Stock(state, n.Item);
                if (miss <= 0)
                {
                    continue;
                }
                sb ??= new StringBuilder();
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(GameText.Format("build.cost.extra_short", n.Item.Name, miss.ToString(CultureInfo.InvariantCulture), SourceOf(n.Item)));
            }
            return sb?.ToString();
        }

        // ── 写进虚影 ──────────────────────────────────────────────────────────────

        /// <summary>把材料清单写进一座虚影（放置 / 升级时）：所需 = 表里的件数，已到 = 0。空清单 = 只要废料（字段清空）。</summary>
        public static void ApplyToSite(BuildingRecord site, IReadOnlyList<BuildMaterialNeed> list)
        {
            if (site == null)
            {
                return;
            }
            int n = list?.Count ?? 0;
            if (n == 0)
            {
                site.ExtraMaterialIds = null;
                site.ExtraRequired = null;
                site.ExtraDelivered = null;
                return;
            }
            site.ExtraMaterialIds = new string[n];
            site.ExtraRequired = new int[n];
            site.ExtraDelivered = new int[n];
            for (int i = 0; i < n; i++)
            {
                site.ExtraMaterialIds[i] = list[i].ResourceType;
                site.ExtraRequired[i] = list[i].Amount;
            }
        }

        /// <summary>这座虚影有几种额外材料（三个平行数组长度不一致时按最短的算，读档容错）。</summary>
        public static int SiteCount(BuildingRecord b)
        {
            if (b?.ExtraMaterialIds == null || b.ExtraRequired == null || b.ExtraDelivered == null)
            {
                return 0;
            }
            return Math.Min(b.ExtraMaterialIds.Length, Math.Min(b.ExtraRequired.Length, b.ExtraDelivered.Length));
        }

        /// <summary>这座建筑投入了几种非废料材料。</summary>
        public static int InvestedCount(BuildingRecord b) =>
            b?.InvestedExtraIds == null || b.InvestedExtraAmounts == null ? 0 : Math.Min(b.InvestedExtraIds.Length, b.InvestedExtraAmounts.Length);

        /// <summary>把一份投入并进建筑的投入账（同种累加）。</summary>
        public static void AddInvested(BuildingRecord b, string resourceType, int amount)
        {
            if (b == null || string.IsNullOrEmpty(resourceType) || amount <= 0)
            {
                return;
            }
            int n = InvestedCount(b);
            for (int i = 0; i < n; i++)
            {
                if (string.Equals(b.InvestedExtraIds[i], resourceType, StringComparison.Ordinal))
                {
                    b.InvestedExtraAmounts[i] += amount;
                    return;
                }
            }
            var ids = new string[n + 1];
            var amounts = new int[n + 1];
            for (int i = 0; i < n; i++)
            {
                ids[i] = b.InvestedExtraIds[i];
                amounts[i] = b.InvestedExtraAmounts[i];
            }
            ids[n] = resourceType;
            amounts[n] = amount;
            b.InvestedExtraIds = ids;
            b.InvestedExtraAmounts = amounts;
        }

        /// <summary>“监听阵列核 ×1、熔炉心 ×1”：建筑里投入的非废料材料（拆除确认框、面板用）；没有返回空串。</summary>
        public static string DescribeInvested(BuildingRecord b)
        {
            int n = InvestedCount(b);
            if (n == 0)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (b.InvestedExtraAmounts[i] <= 0)
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append(GameText.Get("build.cost.extra_sep"));
                }
                sb.Append(Regions.HomeValleyConstruction.MaterialName(b.InvestedExtraIds[i])).Append(" ×")
                  .Append(b.InvestedExtraAmounts[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
