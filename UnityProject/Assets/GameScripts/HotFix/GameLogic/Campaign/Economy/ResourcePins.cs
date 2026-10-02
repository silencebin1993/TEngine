using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-08（FG04 FGR-ECO-051“玩家可以把任意物品固定到顶栏”；卡片“规划助手的结果可以一键固定到顶栏作为目标”）：资源顶栏的固定物品。
    /// 数据在存档 <see cref="ProductionStatsState.Pins"/>（顺序 = 顶栏顺序），可带目标产量；新游戏默认固定废料（<see cref="ProductionStats.Ensure"/>）。
    /// 所有改动走这里（面板按钮、顶栏右键、规划助手“固定为目标”、自检同一入口）；改了 <see cref="Revision"/> +1，顶栏据此重建。
    /// </summary>
    public static class ResourcePins
    {
        public static int MaxPins => Math.Max(1, GridContent.TuningInt("eco.topbar.max_pins"));

        public static int Revision { get; private set; } = 1;

        public static IReadOnlyList<PinnedItemRecord> Pins(CampaignState state) =>
            state == null ? Array.Empty<PinnedItemRecord>() : (IReadOnlyList<PinnedItemRecord>)ProductionStats.Ensure(state).Pins;

        public static PinnedItemRecord Find(CampaignState state, string itemId)
        {
            if (state == null || string.IsNullOrEmpty(itemId))
            {
                return null;
            }
            foreach (PinnedItemRecord p in ProductionStats.Ensure(state).Pins)
            {
                if (p != null && p.ItemId == itemId)
                {
                    return p;
                }
            }
            return null;
        }

        public static bool IsPinned(CampaignState state, string itemId) => Find(state, itemId) != null;

        /// <summary>
        /// 固定一种物品（<paramref name="targetPerMinute"/> 不为空时同时设目标产量；已固定的只更新目标）。超过 eco.topbar.max_pins 种时拒绝并写明（B06）。
        /// 可逆操作，不弹确认（B04）。
        /// </summary>
        public static bool TryPin(CampaignState state, string itemId, float? targetPerMinute, out string message)
        {
            if (state == null || !ItemCatalog.TryGet(itemId, out ItemDef item))
            {
                message = GameText.Get("stats.pin.unknown");
                return false;
            }
            ProductionStatsState st = ProductionStats.Ensure(state);
            PinnedItemRecord existing = Find(state, itemId);
            float target = targetPerMinute.HasValue ? Math.Max(0f, targetPerMinute.Value) : 0f;
            if (existing != null)
            {
                if (!targetPerMinute.HasValue)
                {
                    message = GameText.Format("stats.pin.already", item.Name);
                    return false;
                }
                existing.TargetPerMinute = target;
                Revision++;
                message = target > 0f
                    ? GameText.Format("stats.pin.target_set", item.Name, ProductionStats.Rate(item, target))
                    : GameText.Format("stats.pin.target_cleared", item.Name);
                return true;
            }
            if (st.Pins.Length >= MaxPins)
            {
                message = GameText.Format("stats.pin.full", MaxPins);
                return false;
            }
            var next = new PinnedItemRecord[st.Pins.Length + 1];
            Array.Copy(st.Pins, next, st.Pins.Length);
            next[st.Pins.Length] = new PinnedItemRecord { ItemId = itemId, TargetPerMinute = target };
            st.Pins = next;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.StatsFirstPin);
            message = target > 0f
                ? GameText.Format("stats.pin.done_target", item.Name, ProductionStats.Rate(item, target))
                : GameText.Format("stats.pin.done", item.Name);
            return true;
        }

        public static bool TryUnpin(CampaignState state, string itemId, out string message)
        {
            if (state == null || Find(state, itemId) == null)
            {
                message = GameText.Get("stats.pin.not_pinned");
                return false;
            }
            ProductionStatsState st = ProductionStats.Ensure(state);
            var next = new List<PinnedItemRecord>(st.Pins.Length);
            foreach (PinnedItemRecord p in st.Pins)
            {
                if (p != null && p.ItemId != itemId)
                {
                    next.Add(p);
                }
            }
            st.Pins = next.ToArray();
            Revision++;
            message = GameText.Format("stats.pin.removed", ItemCatalog.NameOf(itemId));
            return true;
        }

        /// <summary>切换（顶栏与面板的“固定 / 取消固定”按钮）。</summary>
        public static bool Toggle(CampaignState state, string itemId, out string message) =>
            IsPinned(state, itemId) ? TryUnpin(state, itemId, out message) : TryPin(state, itemId, null, out message);

        /// <summary>顶栏一格的读数：库存（固体；流体没有家园库存读数 = -1）、窗口产量与净速率、目标与是否达标。</summary>
        public struct PinView
        {
            public ItemDef Item;
            public int Stock;
            public float ProducedPerMinute;
            public float NetPerMinute;
            public float Target;
            /// <summary>FG4-ECO-10：流体在储罐里的存量合计（升；-1 = 管线服务没运行）与装着它的网络数。固体为 -1 / 0。</summary>
            public long FluidLiters;
            public int FluidNetworks;
            public bool HasTarget => Target > 0f;
            public bool Reached => Target > 0f && ProducedPerMinute >= Target * (1f - TargetTolerance);
        }

        /// <summary>达标判定的容差（eco.topbar.target_tolerance，初值 0.05：与 FGT-ECO-008 的 5% 一致）。</summary>
        public static float TargetTolerance => Math.Max(0f, GridContent.Tuning("eco.topbar.target_tolerance"));

        /// <summary>顶栏速率用哪个窗口（eco.topbar.rate_window，初值 1 = 10 分钟：配方周期较长时 1 分钟窗口会跳动）。</summary>
        public static int RateWindow => Math.Max(0, Math.Min(ProductionStats.TierCount - 1, GridContent.TuningInt("eco.topbar.rate_window")));

        public static PinView View(CampaignState state, PinnedItemRecord pin)
        {
            ItemCatalog.TryGet(pin?.ItemId, out ItemDef item);
            var v = new PinView { Item = item, Stock = -1, FluidLiters = -1, Target = pin?.TargetPerMinute ?? 0f };
            if (item == null)
            {
                return v;
            }
            if (item.Form != ItemForm.Fluid)
            {
                v.Stock = HomeInventory.Stock(state, item);
            }
            else
            {
                // FG4-ECO-10（DEBT-FG4ECO08-03）：流体的存量 = 全部储罐里这种流体的合计（升；管线服务没运行时 -1，顶栏照旧写“管线”）。
                long ml = Logistics.PipeNetworkService.FluidStockMl(item.FluidId, out int nets);
                v.FluidLiters = ml < 0 ? -1L : ml / 1000L;
                v.FluidNetworks = nets;
            }
            ProductionStats.ItemRate r = ProductionStats.RateOf(state, RateWindow, item);
            v.ProducedPerMinute = r.ProducedPerMinute;
            v.NetPerMinute = r.NetPerMinute;
            return v;
        }
    }
}
