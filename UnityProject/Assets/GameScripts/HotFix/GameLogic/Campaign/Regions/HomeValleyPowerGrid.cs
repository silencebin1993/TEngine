using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.Sim.Logistics;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;

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
    /// FG3-LOG-06（FG03 FGR-LOG-060、061）起扩展为<b>电塔覆盖 + 子网</b>：电力节点（归还核心自带配电、电塔 T1 / T2，半径见 fg.TbPowerNode）覆盖范围内的建筑
    /// 自动接入；节点之间在范围内自动相连；互不相连的是不同的电网，各自结算（同一套“优先级 → 建筑编号”贪心规则，只是按电网分开）；不在任何覆盖里的用电建筑
    /// 是“未接入电网”（<see cref="BuildingPowerState.Unpowered"/>）。拓扑与分配在 AOT 内核 <see cref="PowerKernel"/> 里做（逐建筑循环只在 Main/Sim）。
    ///
    /// 只在容量/建筑状态/优先级/拓扑变化后调用（脏重算），不逐帧重算，满足"热更层每帧不得 O(建筑数)"
    /// 的性能纪律——这条规则同样适用于本类，调用方（<see cref="HomeValleyController"/>、工作单完工 / 拆除 / 搬迁 / 旋转）负责
    /// 只在真正的状态变化点触发 <see cref="Recompute"/>。按时间的部分（储能积分、曲线采样）见 <see cref="WorldStep"/>：每游戏秒 O(电网数)。</summary>
    public static partial class HomeValleyPowerGrid
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

            /// <summary>FG3-LOG-06：不在任何电力覆盖里的运转中用电建筑。</summary>
            public readonly string[] UnconnectedBuildingIds;

            /// <summary>FG3-LOG-06：电网个数。</summary>
            public readonly int SubnetCount;

            public GridSummary(float totalSupply, float totalDemand, float shortfall,
                string[] brownoutBuildingIds, string[] allocationOrderBuildingIds = null, string[] unconnectedBuildingIds = null, int subnetCount = 0)
            {
                TotalSupply = totalSupply;
                TotalDemand = totalDemand;
                Shortfall = shortfall;
                BrownoutBuildingIds = brownoutBuildingIds ?? Array.Empty<string>();
                AllocationOrderBuildingIds = allocationOrderBuildingIds ?? Array.Empty<string>();
                UnconnectedBuildingIds = unconnectedBuildingIds ?? Array.Empty<string>();
                SubnetCount = subnetCount;
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

        /// <summary>ERD-ECO-002 电网仲裁 + FG3-LOG-06 子网：先按建筑记录重建拓扑（节点连通、建筑接入、电网编号），再在每个电网里按优先级（数字小优先）+ buildingId
        /// 稳定排序（AC-ECO-004）确定性分配供给；分不到的 Operational 消费者进 Brownout，不在任何覆盖里的进 Unpowered（未接入电网）。
        /// 核心自带的基础供电（<see cref="HomeValleyLayout.BaseCoreSupply"/>）进核心所在的电网，不依赖核心建造状态；
        /// 核心 demand（10）恒小于 20，且 "home_valley:core" 在同优先级（1）里字典序最小，必然先分到——“发电机损坏时基础 20 仍供核心”照旧成立。
        /// 信号塔只有在真正 Powered 时才提供 <see cref="HomeValleyLayout.SignalTowerBandwidthBonus"/> 带宽加成。
        /// 电网断开（一个电网的节点散到几个新电网）时发“电网断开”警告；建筑从有电网变成未接入时发“失去电网连接”警告（B05 / B08，同类聚合、可定位）。</summary>
        public static GridSummary Recompute(CampaignState state)
        {
            if (state == null)
            {
                return new GridSummary(0f, 0f, 0f, null);
            }
            PowerKernel kernel = EnsureKernel(state);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BuildEntities(state);
            // FG4-ECO-04：太阳能系数按这一刻的环境定（与世界步同一个纯函数：读档 / 换战役后第一次结算就正确）。
            RefreshEnvironment(state, kernel);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            kernel.Rebuild(_entities, _entityCount);
            PruneStorageSettings(state);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            GridSummary result = ApplyResults(state, topologyChanged: true);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastAssembleMs = (t1 - t0) * f;
            LastKernelRebuildMs = (t2 - t1) * f;
            LastApplyMs = (t3 - t2) * f;
            return result;
        }

        /// <summary>最近一次 <see cref="Recompute"/> 里热更层组装实体表（含排序）的耗时（毫秒）。</summary>
        public static double LastAssembleMs { get; private set; }
        /// <summary>最近一次 <see cref="Recompute"/> 里热更层写回结果的耗时（毫秒）。</summary>
        public static double LastApplyMs { get; private set; }

        /// <summary>最近一次 <see cref="Recompute"/> 里 AOT 内核拓扑重算的耗时（毫秒）。性能自检用它把“内核（AOT）”和“热更层组装 / 写回（真机解释执行）”分开计。</summary>
        public static double LastKernelRebuildMs { get; private set; }

        /// <summary>把内核的结算结果写回建筑记录（PowerState）、汇总到 <see cref="CampaignState"/>，并发出状态翻转的反馈。</summary>
        private static GridSummary ApplyResults(CampaignState state, bool topologyChanged)
        {
            PowerKernel kernel = _kernel;
            var brownout = new List<string>();
            var unconnected = new List<string>();
            var order = new List<string>();
            List<string> newlyLost = null;
            List<string> newlyRestored = null;
            List<string> newlyCut = null;
            // FG0-UX-01（FGR-UX-020 定位）：逐栋记下坐标，通知中心按楼逐条可定位（“3 处缺电”展开后每条都能点）。
            List<UnityEngine.Vector2> lostAt = null;
            List<UnityEngine.Vector2> restoredAt = null;
            List<UnityEngine.Vector2> cutAt = null;
            bool signalTowerPowered = false;
            float totalDemand = 0f;
            int poles = 0;

            for (int i = 0; i < _entityCount; i++)
            {
                BuildingRecord building = _recordAt[i];
                if (building == null)
                {
                    continue;
                }
                if (building.ConstructionState == BuildingConstructionState.Operational && IsPoleType(building.BuildingTypeId))
                {
                    poles++;
                }
                PowerEntity e = _entities[i];
                if (!e.DemandOn)
                {
                    continue;
                }
                totalDemand += e.Demand;
                order.Add(building.BuildingId);
                BuildingPowerState before = building.PowerState;
                PowerUse use = kernel.UseOf(i);
                BuildingPowerState now = use == PowerUse.Powered ? BuildingPowerState.Powered
                    : use == PowerUse.Brownout ? BuildingPowerState.Brownout
                    : BuildingPowerState.Unpowered;
                building.PowerState = now;
                if (before != now)
                {
                    BuildingVisualFeed.Mark(building); // FG3-LOG-09：画面只重画供电状态变了的建筑
                }
                if (now == BuildingPowerState.Powered)
                {
                    if (building.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower)
                    {
                        signalTowerPowered = true;
                    }
                    if (before == BuildingPowerState.Brownout || before == BuildingPowerState.Unpowered)
                    {
                        (newlyRestored ??= new List<string>()).Add(Feedback.FeedbackCues.BuildingLabel(building.BuildingId));
                        (restoredAt ??= new List<UnityEngine.Vector2>()).Add(building.Position);
                    }
                }
                else if (now == BuildingPowerState.Brownout)
                {
                    brownout.Add(building.BuildingId);
                    if (before != BuildingPowerState.Brownout)
                    {
                        (newlyLost ??= new List<string>()).Add(Feedback.FeedbackCues.BuildingLabel(building.BuildingId));
                        (lostAt ??= new List<UnityEngine.Vector2>()).Add(building.Position);
                    }
                }
                else
                {
                    unconnected.Add(building.BuildingId);
                    if (before != BuildingPowerState.Unpowered && !_suppressFeedback)
                    {
                        (newlyCut ??= new List<string>()).Add(Feedback.FeedbackCues.BuildingLabel(building.BuildingId));
                        (cutAt ??= new List<UnityEngine.Vector2>()).Add(building.Position);
                    }
                }
            }

            if (!_suppressFeedback)
            {
                // ER8-CONTENT-01 AC-AUD-001 断电：只在状态真正翻转的那次重算出声（读档/重复重算状态不变，
                // 不会误报）；同一次重算里多栋楼一起停机合并成一条。
                if (newlyLost != null)
                {
                    Feedback.FeedbackCues.RaiseLocatedGroup(Feedback.FeedbackCueId.PowerLost, string.Join("、", newlyLost) + " 停机",
                        newlyLost.Select(n => n + " 停机").ToList(), lostAt);
                    GuidanceHooks.Raise(GuidanceHooks.PowerFirstBrownout);
                }
                if (newlyRestored != null)
                {
                    Feedback.FeedbackCues.RaiseLocatedGroup(Feedback.FeedbackCueId.PowerRestored, string.Join("、", newlyRestored),
                        newlyRestored, restoredAt);
                }
                if (newlyCut != null)
                {
                    for (int i = 0; i < newlyCut.Count; i++)
                    {
                        UnityEngine.Vector2 p = cutAt[i];
                        NotificationCenter.Post("power_unconnected", GameText.Format("power.notify.unconnected", newlyCut[i]), new UnityEngine.Vector3(p.x, 0f, p.y));
                    }
                    LastUnconnectedNotices += newlyCut.Count;
                }
                if (topologyChanged)
                {
                    foreach (PowerSplit split in kernel.LastSplits)
                    {
                        PostSplit(split);
                    }
                }
            }
            if (poles > _lastPoleCount && !_suppressFeedback)
            {
                GuidanceHooks.Raise(GuidanceHooks.PowerPoleFirstPlaced);
            }
            _lastPoleCount = poles;

            float totalSupply = 0f;
            for (int s = 0; s < kernel.SubnetCount; s++)
            {
                totalSupply += kernel.Subnet(s).Supply;
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

            if (_legacyCheckPending)
            {
                _legacyCheckPending = false;
                if (unconnected.Count > 0 && _bindWasLegacySave)
                {
                    // 旧存档（本 Story 之前的电网是全局容量）：离核心太远的建筑读档后没有电——写明原因与办法，不替玩家补电塔。
                    NotificationCenter.Post("save_migrated", GameText.Format("power.notify.legacy", unconnected.Count));
                }
            }

            // FG4-ECO-11（FGR-ECO-020 断电时槽位失效、来电后恢复）：超控阵列的生效等级复用这一个结算出口——完工 / 升级 / 拆除 / 启停 / 摧毁 / 电力翻转都走这里，不另起轮询。
            Signal.OverrideArrayService.OnPowerApplied(state, notify: !_suppressFeedback);
            Economy.TestRangeService.OnPowerApplied(state, notify: !_suppressFeedback); // FG5-RND-03：第一座靶场建成的引导钩子（完工 / 启停都走电网结算）。
            _suppressFeedback = false;
            string[] brownoutIds = brownout.ToArray();
            string[] unconnectedIds = unconnected.ToArray();
            // 汇总缓存（HUD 每帧 O(1) 读，见 TryGetCachedSummary）：结果只在这里变。
            _cachedSummary = new GridSummary(totalSupply, totalDemand, Math.Max(0f, totalDemand - totalSupply),
                brownoutIds, null, unconnectedIds, kernel.SubnetCount);
            _summaryValid = true;
            SummaryVersion++;
            // FG4-ECO-09：远征在外时按“此刻缺电停机的建筑”开 / 关离家报告里的一段停电（O(1)；结算结果只在这里变）。
            Economy.AwayReportService.OnPowerApplied(state, brownoutIds);
            return new GridSummary(totalSupply, totalDemand, Math.Max(0f, totalDemand - totalSupply),
                brownoutIds, order.ToArray(), unconnectedIds, kernel.SubnetCount);
        }

        /// <summary>
        /// FG4-ECO-11（FGR-ECO-020“持续耗电 T1 80，T2 120，T3 160”）：这座建筑现在的耗电——有等级、且这一级在 fg.TbBuildingTier 填了 powerDemand 的按等级，
        /// 其余按建筑表（fg.TbBuilding.powerDemand）。不用电的建筑返回 0。O(1)（两次字典查找）。
        /// </summary>
        public static float DemandOf(BuildingRecord b)
        {
            if (b == null || !HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) p))
            {
                return 0f;
            }
            GameConfig.fg.BuildingTier row = Economy.BuildingOps.HasTiers(b.BuildingTypeId)
                ? Economy.BuildingOps.TierRow(b.BuildingTypeId, Economy.BuildingOps.TierOf(b))
                : null;
            return row != null && row.PowerDemand > 0f ? row.PowerDemand : p.PowerDemand;
        }

        /// <summary>这一类建筑有没有按等级变的耗电（任何一级填了 powerDemand）。</summary>
        public static bool HasTierDemand(string typeId)
        {
            if (typeId == null || !Economy.BuildingOps.HasTiers(typeId))
            {
                return false;
            }
            for (int t = 1; t <= Economy.BuildingOps.MaxTier(typeId); t++)
            {
                GameConfig.fg.BuildingTier row = Economy.BuildingOps.TierRow(typeId, t);
                if (row != null && row.PowerDemand > 0f)
                {
                    return true;
                }
            }
            return false;
        }

        private static void PostSplit(PowerSplit split)
        {
            var parts = new List<string>(split.NewSerials.Length);
            foreach (int serial in split.NewSerials)
            {
                parts.Add(SubnetName(serial));
            }
            UnityEngine.Vector3? at = null;
            // 定位到断出去的那一段（第二段起）的第一个节点：玩家要去看的是“哪里断了”。
            int cut = split.NewSerials.Length > 1 ? _kernel.SubnetIndexOfSerial(split.NewSerials[1]) : -1;
            if (cut >= 0 && _kernel.Subnet(cut).FirstNode >= 0)
            {
                PowerEntity n = _kernel.Entity(_kernel.Subnet(cut).FirstNode);
                at = new UnityEngine.Vector3((n.MinX + n.MaxX) * 0.5f, 0f, (n.MinY + n.MaxY) * 0.5f);
            }
            NotificationCenter.Post("power_split",
                GameText.Format("power.notify.split", SubnetName(split.OldSerial), split.Parts,
                    string.Join(GameText.Language == GameLanguage.En ? ", " : "、", parts)), at);
            GuidanceHooks.Raise(GuidanceHooks.PowerFirstSplit);
            LastSplitNotices++;
        }

        private static GridSummary _cachedSummary;
        private static bool _summaryValid;

        /// <summary>每次结算结果写回（<see cref="Recompute"/> / 储能步进）或解绑 +1。HUD 只在它变化时重拼文本。</summary>
        public static int SummaryVersion { get; private set; }

        /// <summary>FG3-LOG-06：O(1) 读上一次结算缓存的汇总（HUD 每帧用）。<paramref name="state"/> 不是当前绑定的战役时返回 false，
        /// 调用方退回 <see cref="GetSummary"/>（只在没绑定时发生）。</summary>
        public static bool TryGetCachedSummary(CampaignState state, out GridSummary summary)
        {
            if (_summaryValid && state != null && _kernel != null && ReferenceEquals(_state, state))
            {
                summary = _cachedSummary;
                return true;
            }
            summary = default;
            return false;
        }

        /// <summary>只读查询当前电网状态，供 HUD 轮询展示（"显示供给/需求/差额/被停建筑"）——
        /// 直接读取上一次 <see cref="Recompute"/> 缓存在 <see cref="BuildingRecord.PowerState"/>/
        /// <see cref="CampaignState.PowerCapacity"/> 里的结果，不重新仲裁、不写任何状态。查询和
        /// 命令分离。本方法 O(建筑数)：FG3-LOG-06 起 HUD 每帧改读 <see cref="TryGetCachedSummary"/>（O(1)），
        /// 只有战役还没绑定电网时才退回这里；自检用它与缓存对账。</summary>
        public static GridSummary GetSummary(CampaignState state)
        {
            float totalDemand = 0f;
            var brownout = new List<string>();
            var unconnected = new List<string>();
            foreach (BuildingRecord b in state.BuildingRecords)
            {
                if (b.RegionId != HomeValleyLayout.RegionId
                    || b.ConstructionState != BuildingConstructionState.Operational
                    || !HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) profile))
                {
                    continue;
                }
                totalDemand += DemandOf(b);
                if (b.PowerState == BuildingPowerState.Brownout)
                {
                    brownout.Add(b.BuildingId);
                }
                else if (b.PowerState == BuildingPowerState.Unpowered)
                {
                    unconnected.Add(b.BuildingId);
                }
            }

            int subnets = _kernel != null && ReferenceEquals(_state, state) ? _kernel.SubnetCount : 0;
            return new GridSummary(state.PowerCapacity, totalDemand,
                Math.Max(0f, totalDemand - state.PowerCapacity), brownout.ToArray(), null, unconnected.ToArray(), subnets);
        }

        /// <summary>玩家在电网面板改优先级（FG3-LOG-06 起的正式 UI 入口：<c>PowerPanelUIToolkit</c>）。1～4 范围外拒绝，成功后触发一次
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
            if (!HomeValleyLayout.PowerProfile.ContainsKey(building.BuildingTypeId))
            {
                return GridResult.Fail($"not-a-power-consumer:{building.BuildingTypeId}");
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
                BuildingVisualFeed.Mark(building);
            }
            else if (building.ConstructionState == BuildingConstructionState.Disabled)
            {
                building.ConstructionState = BuildingConstructionState.Operational;
                BuildingVisualFeed.Mark(building);
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
