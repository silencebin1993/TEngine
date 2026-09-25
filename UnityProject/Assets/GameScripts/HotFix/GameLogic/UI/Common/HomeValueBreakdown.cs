using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.UI.Kit;

namespace GameLogic.UI.Common
{
    /// <summary>
    /// FG0-UX-01（FGR-UX-030 复合数值可展开来源、FG00 B13、FG-GAP-003）：家园 HUD 上电力与信号带宽两个复合数值的来源展开。
    /// 与电网仲裁 <see cref="HomeValleyPowerGrid.Recompute"/> 读同一份数据（建筑记录 + <see cref="HomeValleyLayout"/> 的供给 / 需求档案，
    /// 均来自 fg.TbBuilding），不另算一套；自检核对“来源逐项相加 == 电网算出的合计”。
    /// 只在玩家把鼠标停在数值上时计算（O(建筑数)，家园建筑个位数），不进每帧路径。
    /// </summary>
    public static class HomeValueBreakdown
    {
        public static TooltipContent Power(CampaignState state)
        {
            var content = new TooltipContent
            {
                Title = GameText.Get("tooltip.power.title"),
                Body = GameText.Get("tooltip.power.body"),
            };
            if (state == null)
            {
                return content;
            }
            float supply = HomeValleyLayout.BaseCoreSupply;
            content.Sources.Add(new TooltipSource(GameText.Get("tooltip.power.base_core"), Signed(HomeValleyLayout.BaseCoreSupply)));
            float demand = 0f;
            var consumers = new List<TooltipSource>();
            foreach (BuildingRecord b in state.BuildingRecords ?? System.Array.Empty<BuildingRecord>())
            {
                if (b.RegionId != HomeValleyLayout.RegionId || b.ConstructionState != BuildingConstructionState.Operational)
                {
                    continue;
                }
                string name = FeedbackCues.BuildingLabel(b.BuildingId);
                if (HomeValleyLayout.PowerSupplyProfile.TryGetValue(b.BuildingTypeId, out float s))
                {
                    supply += s;
                    content.Sources.Add(new TooltipSource(GameText.Format("tooltip.power.supply_of", name), Signed(s)));
                }
                if (HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) p))
                {
                    demand += p.PowerDemand;
                    string key = b.PowerState == BuildingPowerState.Brownout ? "tooltip.power.brownout_of" : "tooltip.power.demand_of";
                    consumers.Add(new TooltipSource(GameText.Format(key, name, p.PowerPriority.ToString(CultureInfo.InvariantCulture)), Signed(-p.PowerDemand)));
                }
            }
            content.Sources.AddRange(consumers);
            float net = supply - demand;
            content.Total = GameText.Format(net >= 0f ? "tooltip.power.surplus" : "tooltip.power.shortfall", UiFormat.Number(System.Math.Abs(net)));
            return content;
        }

        public static TooltipContent Signal(CampaignState state)
        {
            var content = new TooltipContent
            {
                Title = GameText.Get("tooltip.signal.title"),
                Body = GameText.Get("tooltip.signal.body"),
            };
            if (state == null)
            {
                return content;
            }
            float total = HomeValleyLayout.BaseSignalBandwidth;
            content.Sources.Add(new TooltipSource(GameText.Get("tooltip.signal.base"), Signed(HomeValleyLayout.BaseSignalBandwidth)));
            if (HomeValleySignal.TowerContributing(state))
            {
                total += HomeValleyLayout.SignalTowerBandwidthBonus;
                content.Sources.Add(new TooltipSource(GameText.Get("tooltip.signal.tower"), Signed(HomeValleyLayout.SignalTowerBandwidthBonus)));
            }
            else
            {
                content.Sources.Add(new TooltipSource(GameText.Get("tooltip.signal.tower_off"), Signed(0f)));
            }
            if (state.SignalTowerBroadcastOff)
            {
                total -= CampaignExposureLedger.TowerBroadcastOffBandwidthPenalty;
                content.Sources.Add(new TooltipSource(GameText.Get("tooltip.signal.broadcast_off"), Signed(-CampaignExposureLedger.TowerBroadcastOffBandwidthPenalty)));
            }
            content.Total = GameText.Format("ui.tooltip.total", UiFormat.Number(System.Math.Max(0f, total)));
            return content;
        }

        /// <summary>来源逐项相加（自检用：必须等于电网 / 信号算出的合计）。</summary>
        public static float SumSources(TooltipContent content)
        {
            float sum = 0f;
            foreach (TooltipSource s in content.Sources)
            {
                if (float.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                {
                    sum += v;
                }
            }
            return sum;
        }

        private static string Signed(float v) =>
            (v > 0f ? "+" : string.Empty) + v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
