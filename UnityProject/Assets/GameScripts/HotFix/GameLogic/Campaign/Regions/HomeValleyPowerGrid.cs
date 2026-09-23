using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER3-PWR-01：归还谷地电网仲裁的唯一入口。取代 ER2-SCENE-01 遗留的
    /// <c>HomeValleyController.RecomputePower</c> 最小实现——原实现把
    /// <see cref="CampaignState.PowerCapacity"/> 当成"只增不减的历史累加值"（<c>CompleteRepair</c>
    /// 里 <c>+= 80f</c>），没有"来源"概念，无法支持"发电机损坏/被关停时精确撤销其贡献"这个
    /// ERD-ECO-002 要求的场景。本类改为每次都从 <see cref="HomeValleyLayout.BaseCoreSupply"/> +
    /// 当前所有 Operational 供给类建筑动态求和，<see cref="CampaignState.PowerCapacity"/> 变成
    /// 只读派生值，调用方不应再直接对它做 +=/-=。
    ///
    /// 只覆盖归还谷地目前存在的建筑类型；跨区域的更完整仲裁属于后续 Story（信标区域接入等）。
    /// 只在容量/建筑状态/优先级变化后调用（脏重算），不逐帧重算，满足"热更层每帧不得 O(建筑数)"
    /// 的性能纪律——这条规则同样适用于本类，调用方（<see cref="HomeValleyController"/>）负责
    /// 只在真正的状态变化点触发 <see cref="Recompute"/>。</summary>
    public static class HomeValleyPowerGrid
    {
        public readonly struct GridSummary
        {
            public readonly float TotalSupply;
            public readonly float TotalDemand;
            public readonly float Shortfall;
            public readonly string[] BrownoutBuildingIds;

            /// <summary>consumers 按"优先级（小优先）+ buildingId 稳定排序"（AC-ECO-004）算出的实际
            /// 分配顺序，无论最终是否 Powered 都会出现在这里——供测试/调试断言排序算法本身是否正确，
            /// 不依赖某个具体的供给/需求数值组合是否恰好产生 Brownout。<see cref="GetSummary"/> 不
            /// 重新排序（只读上次 Recompute 的缓存结果），本字段恒为空数组。</summary>
            public readonly string[] AllocationOrderBuildingIds;

            public GridSummary(float totalSupply, float totalDemand, float shortfall,
                string[] brownoutBuildingIds, string[] allocationOrderBuildingIds = null)
            {
                TotalSupply = totalSupply;
                TotalDemand = totalDemand;
                Shortfall = shortfall;
                BrownoutBuildingIds = brownoutBuildingIds ?? Array.Empty<string>();
                AllocationOrderBuildingIds = allocationOrderBuildingIds ?? Array.Empty<string>();
            }
        }

        public readonly struct GridResult
        {
            public readonly bool Success;
            public readonly string FailureReason;

            private GridResult(bool success, string failureReason)
            {
                Success = success;
                FailureReason = failureReason;
            }

            public static GridResult Ok() => new GridResult(true, null);
            public static GridResult Fail(string reason) => new GridResult(false, reason);
        }

        /// <summary>ERD-ECO-002 电网仲裁：按优先级（数字小优先）+ buildingId 稳定排序（AC-ECO-004）
        /// 确定性分配供给；分不到的 Operational 消费者进 Brownout。供给侧动态计算（见类注释），
        /// 不依赖任何历史累加状态，天然满足"发电机损坏时基础 20 仍供核心"——核心 demand（10）
        /// 恒小于 <see cref="HomeValleyLayout.BaseCoreSupply"/>（20），且 BuildingId
        /// "home_valley:core" 在同优先级（1）的消费者里字典序最小，必然排在最前先分配到。
        /// 信号塔只有在真正 Powered（不是 Brownout/Unpowered/Disabled/Damaged）时才提供
        /// <see cref="HomeValleyLayout.SignalTowerBandwidthBonus"/> 带宽加成——断电即同步降低带宽，
        /// 不需要额外的"扣减"步骤。</summary>
        public static GridSummary Recompute(CampaignState state)
        {
            float totalSupply = HomeValleyLayout.BaseCoreSupply;
            foreach (BuildingRecord b in state.BuildingRecords)
            {
                if (b.RegionId == HomeValleyLayout.RegionId
                    && b.ConstructionState == BuildingConstructionState.Operational
                    && HomeValleyLayout.PowerSupplyProfile.TryGetValue(b.BuildingTypeId, out float supply))
                {
                    totalSupply += supply;
                }
            }

            List<BuildingRecord> consumers = state.BuildingRecords
                .Where(b => b.RegionId == HomeValleyLayout.RegionId
                    && b.ConstructionState == BuildingConstructionState.Operational
                    && HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId))
                .OrderBy(b => b.PowerPriority)
                .ThenBy(b => b.BuildingId, StringComparer.Ordinal)
                .ToList();

            float remaining = totalSupply;
            float totalDemand = 0f;
            bool signalTowerPowered = false;
            var brownout = new List<string>();

            foreach (BuildingRecord building in consumers)
            {
                float need = HomeValleyLayout.PowerProfile[building.BuildingTypeId].PowerDemand;
                totalDemand += need;
                if (remaining >= need)
                {
                    building.PowerState = BuildingPowerState.Powered;
                    remaining -= need;
                    if (building.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower)
                    {
                        signalTowerPowered = true;
                    }
                }
                else
                {
                    building.PowerState = BuildingPowerState.Brownout;
                    brownout.Add(building.BuildingId);
                }
            }

            state.PowerCapacity = totalSupply;
            state.PowerDemand = totalDemand;
            // ER6-EXPOSE-01："家园关闭信号塔主动广播……带宽减少3"——玩家主动开关（与本方法自己算出的
            // signalTowerPowered 供电状态完全独立的另一维度），带宽唯一真相仍在本方法集中计算，
            // CampaignExposureLedger 本身不碰这个字段。
            float broadcastOffPenalty = state.SignalTowerBroadcastOff ? CampaignExposureLedger.TowerBroadcastOffBandwidthPenalty : 0f;
            state.SignalBandwidth = Math.Max(0f, HomeValleyLayout.BaseSignalBandwidth
                + (signalTowerPowered ? HomeValleyLayout.SignalTowerBandwidthBonus : 0f)
                - broadcastOffPenalty);

            return new GridSummary(totalSupply, totalDemand, Math.Max(0f, totalDemand - totalSupply),
                brownout.ToArray(), consumers.Select(b => b.BuildingId).ToArray());
        }

        /// <summary>只读查询当前电网状态，供 HUD 轮询展示（"显示供给/需求/差额/被停建筑"）——
        /// 直接读取上一次 <see cref="Recompute"/> 缓存在 <see cref="BuildingRecord.PowerState"/>/
        /// <see cref="CampaignState.PowerCapacity"/> 里的结果，不重新仲裁、不写任何状态。查询和
        /// 命令分离：HUD 每帧调用本方法不会违反"热更层每帧不得 O(建筑数) 触发脏重算"的性能纪律
        /// （这里确实是每帧 O(建筑数)，但只读遍历，不是仲裁；归还谷地建筑数量级恒定个位数）。</summary>
        public static GridSummary GetSummary(CampaignState state)
        {
            float totalDemand = 0f;
            var brownout = new List<string>();
            foreach (BuildingRecord b in state.BuildingRecords)
            {
                if (b.RegionId != HomeValleyLayout.RegionId
                    || b.ConstructionState != BuildingConstructionState.Operational
                    || !HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) profile))
                {
                    continue;
                }
                totalDemand += profile.PowerDemand;
                if (b.PowerState == BuildingPowerState.Brownout)
                {
                    brownout.Add(b.BuildingId);
                }
            }

            return new GridSummary(state.PowerCapacity, totalDemand,
                Math.Max(0f, totalDemand - state.PowerCapacity), brownout.ToArray());
        }

        /// <summary>玩家在建筑面板改优先级（正式 UI 交互入口留 ER5-INT-01/UI-04，见
        /// STORY-EXECUTION-CARDS.md #ER3-PWR-01 范围裁剪说明）。1～4 范围外拒绝，成功后触发一次
        /// 脏重算（"所有改变只触发脏重算"——不逐帧轮询）。</summary>
        public static GridResult TrySetPriority(CampaignState state, string buildingId, int priority)
        {
            if (state == null || string.IsNullOrEmpty(buildingId))
            {
                return GridResult.Fail("invalid-args");
            }
            if (priority < 1 || priority > 4)
            {
                return GridResult.Fail($"priority-out-of-range:{priority}");
            }

            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == buildingId);
            if (building == null)
            {
                return GridResult.Fail($"building-not-found:{buildingId}");
            }

            building.PowerPriority = priority;
            Recompute(state);
            return GridResult.Ok();
        }

        /// <summary>玩家主动关停/重新启用一个允许关停的消费者（"允许关停"＝存在于
        /// <see cref="HomeValleyLayout.PowerProfile"/> 里且不是核心——核心是电网的仲裁基准，
        /// 关停核心没有意义也会让"核心永不断电"的不变量失去锚点）。复用既有
        /// <see cref="BuildingConstructionState.Disabled"/> 枚举值，不新增字段：Disabled 状态的
        /// 建筑天然被 <see cref="Recompute"/> 的 Operational 过滤条件排除，不参与仲裁。只允许在
        /// Operational/Disabled 之间切换——Damaged/Building 等状态的建筑本来就不在电网里，切换
        /// 关停对它们没有意义，直接拒绝。</summary>
        public static GridResult TryToggleShutdown(CampaignState state, string buildingId)
        {
            if (state == null || string.IsNullOrEmpty(buildingId))
            {
                return GridResult.Fail("invalid-args");
            }

            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == buildingId);
            if (building == null)
            {
                return GridResult.Fail($"building-not-found:{buildingId}");
            }
            if (building.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                return GridResult.Fail("core-cannot-be-shutdown");
            }
            if (!HomeValleyLayout.PowerProfile.ContainsKey(building.BuildingTypeId))
            {
                return GridResult.Fail($"not-a-power-consumer:{building.BuildingTypeId}");
            }

            if (building.ConstructionState == BuildingConstructionState.Operational)
            {
                building.ConstructionState = BuildingConstructionState.Disabled;
            }
            else if (building.ConstructionState == BuildingConstructionState.Disabled)
            {
                building.ConstructionState = BuildingConstructionState.Operational;
            }
            else
            {
                return GridResult.Fail($"cannot-toggle-from:{building.ConstructionState}");
            }

            Recompute(state);
            return GridResult.Ok();
        }
    }
}
