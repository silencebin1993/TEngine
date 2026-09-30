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
    /// FG3-LOG-03（FGR-LOG-024“物品种类由表定义，传送带可以运输任意固体物品”）：传送带物品编号 ↔ 家园资源类型的临时映射。
    /// 内核只认 ushort 物品编号；FG4-ECO-01 物品表落地前，家园仓库只存废料一种（<see cref="HomeValleyCargo.CanStore"/>），
    /// 废料在传送带上的编号由调参 logistics.item.scrap_id 给出（初值 1），其余编号按“物品 #编号”显示、落地时记 "item:编号"（DEBT-FG3LOG02-01）。
    /// 全部方法 O(1)（名字格式化 O(种类数)），不在每帧路径上逐物品调用。
    /// </summary>
    public static class BeltItems
    {
        /// <summary>废料在传送带上的物品编号（表：logistics.item.scrap_id）。</summary>
        public static ushort ScrapId => (ushort)Math.Max(1, Math.Min(ushort.MaxValue - 1, GridContent.TuningInt("logistics.item.scrap_id")));

        /// <summary>物品编号 → 家园资源类型（废料 = "scrap"，其余 = "item:编号"）。</summary>
        public static string ResourceOf(ushort item) =>
            item == ScrapId ? CampaignEconomyLedger.ResourceScrap : "item:" + item.ToString(CultureInfo.InvariantCulture);

        /// <summary>家园仓库能不能存这种物品（目前只有废料）。</summary>
        public static bool IsStorable(ushort item) => HomeValleyCargo.CanStore(ResourceOf(item));

        /// <summary>玩家看到的物品名（当前语言）。</summary>
        public static string Name(ushort item) => HomeValleyConstruction.MaterialName(ResourceOf(item));

        /// <summary>家园仓库现在能存的全部物品（端口面板的输出过滤选项、仓库输出口“全部”时推哪种）。FG4-ECO-01 起按物品表列出。</summary>
        public static void CollectStorable(List<ushort> into)
        {
            into.Clear();
            into.Add(ScrapId);
        }

        /// <summary>仓库里某种物品的库存（目前只有废料有真实持有量）。</summary>
        public static int Stock(CampaignState state, ushort item) =>
            state != null && item == ScrapId ? Math.Max(0, state.Scrap) : 0;

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
