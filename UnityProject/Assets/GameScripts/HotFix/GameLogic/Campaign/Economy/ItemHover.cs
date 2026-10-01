using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-01（FG04 第 4 节“悬停物品图标显示：总库存、各仓库分布、当前净速率”）：物品悬停提示的文字（数据来自 <see cref="ItemDistribution"/> 缓存与
    /// <see cref="ItemFlowStats"/> 采样，不现算）。界面（物资面板等任何画物品图标的地方）用它填悬停提示，同一种物品处处同一写法。
    /// </summary>
    public static class ItemHover
    {
        /// <summary>数量文字：固体 / 数字资源按件，流体按升。</summary>
        public static string AmountText(ItemDef item, long n) =>
            item != null && item.Form == ItemForm.Fluid
                ? GameText.Format("codex.recipe.entry_fluid", string.Empty, n.ToString(CultureInfo.InvariantCulture)).Trim()
                : n.ToString(CultureInfo.InvariantCulture);

        /// <summary>第一行：总库存（固体 / 保管库附带仓库可用 / 容量；流体按升）。</summary>
        public static string TotalLine(CampaignState state, ItemDef item, ItemDistributionView view)
        {
            long total = view?.Total ?? 0;
            if (total <= 0)
            {
                return GameText.Get("item.hover.none");
            }
            switch (item.Form)
            {
                case ItemForm.Solid:
                case ItemForm.Vault:
                    return GameText.Format("item.hover.total_cap", total.ToString(CultureInfo.InvariantCulture),
                        (view?.Stock ?? 0).ToString(CultureInfo.InvariantCulture), (view?.Capacity ?? 0).ToString(CultureInfo.InvariantCulture));
                case ItemForm.Fluid:
                    return GameText.Format("item.hover.total_fluid", total.ToString(CultureInfo.InvariantCulture));
                default:
                    return GameText.Format("item.hover.total", total.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>第二行：净速率（家园库存的首尾差 ÷ 游戏分钟）；还没攒够采样写“测量中”；流体 / 实体写“不统计”。</summary>
        public static string RateLine(CampaignState state, ItemDef item)
        {
            if (!ItemFlowStats.IsTracked(item))
            {
                return GameText.Get("item.hover.rate_none");
            }
            if (ItemFlowStats.TryNetPerMinute(state, item, GameClock.StepHz, out float perMinute, out float window, out float wait))
            {
                string sign = perMinute > 0.05f ? "+" : string.Empty;
                return GameText.Format("item.hover.rate", sign + perMinute.ToString("0.#", CultureInfo.InvariantCulture),
                    Math.Round(window).ToString(CultureInfo.InvariantCulture));
            }
            return GameText.Format("item.hover.rate_measuring", Math.Ceiling(wait).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>分布（标签、数量），顺序固定；在途的标签写明“不算库存”。</summary>
        public static void Parts(ItemDef item, ItemDistributionView view, List<KeyValuePair<string, string>> into)
        {
            into.Clear();
            if (view == null)
            {
                return;
            }
            foreach (KeyValuePair<string, long> p in view.Parts)
            {
                into.Add(new KeyValuePair<string, string>(GameText.Get(p.Key), AmountText(item, p.Value)));
            }
        }
    }
}
