using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>
    /// FG3-LOG-03（FGR-LOG-024“物品种类由表定义，传送带可以运输任意固体物品”）→ FG4-ECO-01：传送带物品编号 ↔ 物品表（fg.TbEcoItem.beltId）。
    /// 内核只认 ushort 物品编号；名字、能不能进家园仓库、库存都按物品表（<see cref="Economy.ItemCatalog"/> / <see cref="Economy.HomeInventory"/>）。
    /// 废料的编号仍由调参 logistics.item.scrap_id 给出（初值 1，物品表载入时核对一致）。表里没有的编号按“未知物品 #编号”显示、落地记 "item:编号"。
    /// 全部方法 O(1)（可存物品列表 O(种类数)），不在每帧路径上逐物品调用。
    /// </summary>
    public static class BeltItems
    {
        /// <summary>废料在传送带上的物品编号（表：logistics.item.scrap_id）。</summary>
        public static ushort ScrapId => (ushort)Math.Max(1, Math.Min(ushort.MaxValue - 1, GridContent.TuningInt("logistics.item.scrap_id")));

        /// <summary>物品编号 → 家园资源类型（废料 = "Scrap"，表里的物品 = 物品 ID，其余 = "item:编号"）。</summary>
        public static string ResourceOf(ushort item)
        {
            if (item == ScrapId)
            {
                return CampaignEconomyLedger.ResourceScrap;
            }
            return Economy.ItemCatalog.TryGetByBelt(item, out Economy.ItemDef d)
                ? d.ResourceType
                : "item:" + item.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>编号 → 物品表行（表里没有返回 null）。</summary>
        public static Economy.ItemDef Def(ushort item) => Economy.ItemCatalog.TryGetByBelt(item, out Economy.ItemDef d) ? d : null;

        /// <summary>家园仓库能不能存这种物品（物品表里的固体）。</summary>
        public static bool IsStorable(ushort item) => Economy.HomeInventory.IsBeltStorable(Def(item));

        /// <summary>玩家看到的物品名（当前语言）。</summary>
        public static string Name(ushort item)
        {
            Economy.ItemDef d = Def(item);
            return d != null ? d.Name : GameText.Format("item.unknown", item.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>家园仓库现在能存的全部物品（端口面板的输出过滤选项、分流器过滤选项）：物品表里全部上传送带的固体，按表顺序。</summary>
        public static void CollectStorable(List<ushort> into)
        {
            into.Clear();
            foreach (Economy.ItemDef d in Economy.ItemCatalog.Items)
            {
                if (Economy.HomeInventory.IsBeltStorable(d))
                {
                    into.Add(d.BeltId);
                }
            }
            if (into.Count == 0)
            {
                into.Add(ScrapId); // 物品表没加载上（已记 Error）：至少保留废料，旧流程不断
            }
        }

        /// <summary>仓库里某种物品的库存。</summary>
        public static int Stock(CampaignState state, ushort item) =>
            item == ScrapId ? Math.Max(0, state?.Scrap ?? 0) : Economy.HomeInventory.Stock(state, Def(item));

        /// <summary>“废料 ×3、物品 #7 ×1”（按编号升序，同一状态同一文字）。</summary>
        public static string FormatCounts(IEnumerable<KeyValuePair<ushort, int>> counts)
        {
            var sorted = new List<KeyValuePair<ushort, int>>();
            foreach (KeyValuePair<ushort, int> kv in counts)
            {
                if (kv.Value > 0)
                {
                    sorted.Add(kv);
                }
            }
            sorted.Sort((a, b) => a.Key.CompareTo(b.Key));
            var sb = new StringBuilder();
            string sep = GameText.Language == GameLanguage.En ? ", " : "、";
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(sep);
                }
                sb.Append(GameText.Format("logistics.hover.item_entry", Name(sorted[i].Key), sorted[i].Value.ToString(CultureInfo.InvariantCulture)));
            }
            return sb.ToString();
        }
    }
}
