using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Content;
using TEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER6-ANA-01 STORY-EXECUTION-CARDS.md：解析台唯一队列状态机——<see cref="AnalysisQueueItemRecord"/>
    /// 的唯一写入口，结构镜像 <see cref="Primitive.PrimitiveCraftStation"/>（单 Running 工位 FIFO、按
    /// <c>CreatedTick</c> 排序、断电转 WaitingPower 且保留进度、建筑被毁全部转 Failed）。
    ///
    /// ── 与解析台"材料"的关系（不是仓内实例，不需要预留机制） ──
    /// <see cref="Primitive.PrimitiveCraftStation"/> 的材料是 <see cref="Primitive.PrimitiveChipRecord"/>
    /// 仓内实例，可能同时被多个操作抢——需要 <c>ReservedByTransactionId</c> 互斥锁。解析台的"材料"是
    /// <see cref="RegionQuestItemRecord"/>（已 Recovered 的关键模块），结构上每个 <c>SalvageInstanceId</c>
    /// 只会被"成功入队一次"（<see cref="TryEnqueue"/> 拒绝为同一实例重复创建非终态队列项），不需要额外
    /// 互斥锁——"不让同一 salvageInstanceId 同时装车和解析"这条硬要求由 <see cref="RegionQuestItemRecord.State"/>
    /// 的单向状态机（Carried→Recovered 后不会再变回 Carried/OnGround，见该类型类注释）结构性保证。
    ///
    /// ── 五态与产出数值权威来源 ──
    /// "货舱/地面"＝<see cref="RegionQuestItemRecord.Carried"/>/<see cref="RegionQuestItemState.OnGround"/>
    /// （区域内，未改动）；"仓库"＝Recovered 且无非终态/Completed 队列项引用；"搬运中"＝本类队列项
    /// State==Queued；"解析台"＝State==Running。解锁目标/技术数据/时长见 <see cref="YieldTable"/>，
    /// 技术数据数值来自 DEMO-CONTENT-LOCK.md §2.5"技术数据最小闭环"（唯一点名数字的地方，时长该文档
    /// 未点名，本类按"数值越高价值的模块解析越久"取保守默认值，同类取舍见 <see cref="YieldTable"/>
    /// 各条注释）。
    ///
    /// ── "通过 WorkOrder 送解析台"的取舍（记录理由，不是静默绕过）──
    /// 验收卡字面写"通过 WorkOrder"；但 <see cref="Regions.HomeValleyWorkOrders.TryCreateHaul"/> 的
    /// Haul 唯一实现严格绑定 <see cref="GroundItemRecord"/>（资源类型+数量，有真实世界坐标可走机器
    /// 搬运路径）——关键模块是 <see cref="RegionQuestItemRecord"/>（带内容 ID 的唯一实例，Recovered
    /// 之后没有家园侧世界坐标，不是"地面上一堆废料"）。把它硬塞进 <c>GroundItemRecord</c> 会丢失
    /// ContentId 这一决定"解析产出什么"的关键字段；改造 Haul 支持第二种搬运对象是对一个已充分验证、
    /// 复杂度不低的既有系统做侵入式变更，收益（换一个队列词汇）远小于风险（回归既有搬运/仓储流程）。
    /// 本类因此走与 <see cref="Primitive.PrimitiveCraftStation"/>"材料送站台"同一precedent（该 Story
    /// 同样不经 WorkOrder，已验收 Completed）：<see cref="TryEnqueue"/> 是本类自己的正式排队事务，
    /// FIFO/单工位/断电暂停/建筑被毁全部按 WorkOrder 同等纪律实现，只是不共用 <c>WorkOrderRecord</c>
    /// 这个具体类型——五态要求的"搬运中"由 <see cref="AnalysisQueueState.Queued"/> 承载（见上）。</summary>
    public static class HomeValleyAnalysis
    {
        public readonly struct YieldInfo
        {
            public readonly string UnlockContentId;
            public readonly int TechDataYield;
            public readonly float Duration;
            public readonly string DisplayName;

            public YieldInfo(string unlockContentId, int techDataYield, float duration, string displayName)
            {
                UnlockContentId = unlockContentId;
                TechDataYield = techDataYield;
                Duration = duration;
                DisplayName = displayName;
            }
        }

        /// <summary>解析目标唯一权威表——键是 <see cref="RegionQuestItemRecord.ContentId"/>（战场原始模块
        /// 标识），值是解析完成后真正解锁的可装配内容 ID（进 <see cref="CampaignState.UnlockedContentIds"/>）
        /// + 技术数据产出 + 时长。ER6-FOUNDRY-01 铸造重炮掉落落地后应在此追加
        /// <c>quest_cannon_module → (ComponentCatalog.CompCannonId, 15, ...)</c> 一行，不需要改动本类
        /// 其余逻辑。</summary>
        public static readonly IReadOnlyDictionary<string, YieldInfo> YieldTable = new Dictionary<string, YieldInfo>
        {
            // DEMO-CONTENT-LOCK.md §2.5："解析静默标记器 +8"。
            [FracturedCityLayout.MarkerModuleContentId] =
                new YieldInfo(ComponentCatalog.FuncMarkerId, 8, 10f, "静默标记器"),
            // DEMO-CONTENT-LOCK.md §2.5："解析标记跳转协议数据盒 +12"。
            [FracturedCityLayout.ProtocolDataboxContentId] =
                new YieldInfo(FirmwareCatalog.FwMarkTagId, 12, 15f, "标记跳转固件"),
        };

        /// <summary>Demo 关键物总数个位数（当前 2 件，未来 ER6-FOUNDRY-01 追加 1 件），队列上限给一点
        /// 余量即可，不需要 <see cref="Primitive.PrimitiveCraftStation.MaxActiveQueueItems"/> 那种量级。</summary>
        public const int MaxActiveQueueItems = 5;

        public readonly struct AnalysisOpResult
        {
            public readonly bool Success;
            public readonly string FailureReason;
            public readonly string QueueItemId;

            private AnalysisOpResult(bool success, string failureReason, string queueItemId)
            {
                Success = success;
                FailureReason = failureReason;
                QueueItemId = queueItemId;
            }

            public static AnalysisOpResult Ok(string queueItemId) => new AnalysisOpResult(true, null, queueItemId);
            public static AnalysisOpResult Fail(string reason) => new AnalysisOpResult(false, reason, null);
        }

        private static readonly AnalysisQueueState[] ActiveStates =
        {
            AnalysisQueueState.Queued, AnalysisQueueState.Running, AnalysisQueueState.WaitingPower,
        };

        private static bool IsActive(AnalysisQueueState s) => Array.IndexOf(ActiveStates, s) >= 0;
        private static bool IsHeadCandidate(AnalysisQueueState s) => IsActive(s);

        public static AnalysisQueueItemRecord Find(CampaignState state, string queueItemId) =>
            state?.AnalysisQueues?.FirstOrDefault(q => q.QueueItemId == queueItemId);

        /// <summary>某个已 Recovered 的关键物当前是否已经"在办"（搬运中/解析台/已完成）——供仓库列表
        /// 过滤（只显示还没送去解析的），也供 <see cref="TryEnqueue"/> 拒绝重复入队。</summary>
        public static bool HasActiveOrCompletedQueueItem(CampaignState state, string salvageInstanceId) =>
            state?.AnalysisQueues != null && state.AnalysisQueues.Any(q =>
                q.SalvageInstanceId == salvageInstanceId &&
                (IsActive(q.State) || q.State == AnalysisQueueState.Completed));

        /// <summary>"仓库"态：已 Recovered、有产出表项、且当前没有非终态/已完成队列项引用——玩家可选送
        /// 解析台的完整清单。</summary>
        public static List<RegionQuestItemRecord> WarehouseItems(CampaignState state)
        {
            var result = new List<RegionQuestItemRecord>();
            if (state?.RegionQuestItems == null)
            {
                return result;
            }
            foreach (RegionQuestItemRecord item in state.RegionQuestItems)
            {
                if (item.State != RegionQuestItemState.Recovered || !YieldTable.ContainsKey(item.ContentId))
                {
                    continue;
                }
                if (HasActiveOrCompletedQueueItem(state, item.SalvageInstanceId))
                {
                    continue;
                }
                result.Add(item);
            }
            return result;
        }

        private static long NowTick(CampaignState state) => (long)(state.PlaySeconds * 1000f);

        private static int ActiveCount(CampaignState state) =>
            state.AnalysisQueues?.Count(q => IsActive(q.State)) ?? 0;

        // ── 入队 ─────────────────────────────────────────────────────────────────

        public static AnalysisOpResult TryEnqueue(CampaignState state, string salvageInstanceId)
        {
            if (state == null || string.IsNullOrEmpty(salvageInstanceId))
            {
                return AnalysisOpResult.Fail("invalid-args");
            }
            RegionQuestItemRecord item = state.RegionQuestItems?.FirstOrDefault(q => q.SalvageInstanceId == salvageInstanceId);
            if (item == null)
            {
                return AnalysisOpResult.Fail("item-not-found");
            }
            if (item.State != RegionQuestItemState.Recovered)
            {
                return AnalysisOpResult.Fail("item-not-recovered");
            }
            if (!YieldTable.TryGetValue(item.ContentId, out YieldInfo info))
            {
                return AnalysisOpResult.Fail("no-yield-defined");
            }
            if (HasActiveOrCompletedQueueItem(state, salvageInstanceId))
            {
                return AnalysisOpResult.Fail("already-queued-or-analyzed");
            }
            if (ActiveCount(state) >= MaxActiveQueueItems)
            {
                return AnalysisOpResult.Fail("queue-full");
            }

            string queueItemId = "analysis:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var queueItem = new AnalysisQueueItemRecord
            {
                QueueItemId = queueItemId,
                SalvageInstanceId = salvageInstanceId,
                ContentId = item.ContentId,
                Duration = info.Duration,
                Progress = 0f,
                State = AnalysisQueueState.Queued,
                CreatedTick = NowTick(state),
            };
            state.AnalysisQueues = (state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>()).Append(queueItem).ToArray();
            return AnalysisOpResult.Ok(queueItemId);
        }

        // ── 取消 ─────────────────────────────────────────────────────────────────

        /// <summary>取消：不消耗任何前置资源（入队不扣废料/不锁材料实例——"材料"就是
        /// <see cref="RegionQuestItemRecord"/> 本身，Recovered 状态不受本类任何操作影响），取消后该
        /// 关键物立即重新出现在 <see cref="WarehouseItems"/> 里，可再次入队。</summary>
        public static AnalysisOpResult TryCancel(CampaignState state, string queueItemId)
        {
            AnalysisQueueItemRecord item = Find(state, queueItemId);
            if (item == null)
            {
                return AnalysisOpResult.Fail($"not-found:{queueItemId}");
            }
            if (!IsActive(item.State))
            {
                return AnalysisOpResult.Fail($"cannot-cancel-from:{item.State}");
            }
            item.State = AnalysisQueueState.Cancelled;
            item.BlockedReason = null;
            return AnalysisOpResult.Ok(queueItemId);
        }

        // ── 每帧驱动 ─────────────────────────────────────────────────────────────

        /// <summary>由 <see cref="HomeValleyController.Update"/> 每帧调用一次（队列量级恒定个位数，
        /// 同 <see cref="Primitive.PrimitiveCraftStation.Tick"/> 性能纪律）。</summary>
        public static void Tick(CampaignState state, float dt)
        {
            if (state == null || state.AnalysisQueues == null || state.AnalysisQueues.Length == 0)
            {
                return;
            }

            BuildingRecord bench = state.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAnalysisBench);
            bool benchDestroyed = bench == null || bench.ConstructionState == BuildingConstructionState.Destroyed;
            if (benchDestroyed)
            {
                foreach (AnalysisQueueItemRecord it in state.AnalysisQueues)
                {
                    if (!IsActive(it.State))
                    {
                        continue;
                    }
                    it.State = AnalysisQueueState.Failed;
                    it.BlockedReason = "analysis-bench-destroyed";
                }
                return;
            }

            AnalysisQueueItemRecord head = FindHead(state);
            if (head != null)
            {
                TickHead(state, head, dt, bench);
            }
        }

        private static AnalysisQueueItemRecord FindHead(CampaignState state)
        {
            AnalysisQueueItemRecord head = null;
            foreach (AnalysisQueueItemRecord it in state.AnalysisQueues)
            {
                if (!IsHeadCandidate(it.State))
                {
                    continue;
                }
                if (head == null || it.CreatedTick < head.CreatedTick
                    || (it.CreatedTick == head.CreatedTick && string.CompareOrdinal(it.QueueItemId, head.QueueItemId) < 0))
                {
                    head = it;
                }
            }
            return head;
        }

        private static void TickHead(CampaignState state, AnalysisQueueItemRecord item, float dt, BuildingRecord bench)
        {
            bool powered = bench.ConstructionState == BuildingConstructionState.Operational
                && bench.PowerState == BuildingPowerState.Powered;

            if (item.State == AnalysisQueueState.Running)
            {
                if (!powered)
                {
                    item.State = AnalysisQueueState.WaitingPower;
                    item.BlockedReason = "analysis-bench-unpowered";
                    return; // 断电：Progress 原样保留，不倒退（AC-ECO-003"断电暂停而不倒退"）。
                }
                item.Progress += dt;
                if (item.Progress >= item.Duration)
                {
                    Complete(state, item);
                }
                return;
            }

            // Queued / WaitingPower：逐帧尝试开工。
            if (!powered)
            {
                item.State = AnalysisQueueState.WaitingPower;
                item.BlockedReason = "analysis-bench-unpowered";
                return;
            }
            item.State = AnalysisQueueState.Running;
            item.BlockedReason = null;
        }

        /// <summary>完成解析：二次核验关键物仍是 Recovered（理论上不会变，防御性核验同
        /// <see cref="Primitive.PrimitiveCraftStation.CompleteCraft"/> 先例），写解锁+技术数据。
        /// 幂等：<see cref="MechanicalContentUnlock"/> 用 <see cref="CampaignState.UnlockedContentIds"/>
        /// 去重判定，本方法追加前先查一遍避免同一内容出现两条完全相同的 ID（正常路径下不会发生，
        /// 双重防御无害）。</summary>
        private static void Complete(CampaignState state, AnalysisQueueItemRecord item)
        {
            item.Progress = item.Duration;

            RegionQuestItemRecord questItem = state.RegionQuestItems?.FirstOrDefault(q => q.SalvageInstanceId == item.SalvageInstanceId);
            if (questItem == null || questItem.State != RegionQuestItemState.Recovered
                || !YieldTable.TryGetValue(item.ContentId, out YieldInfo info))
            {
                item.State = AnalysisQueueState.Failed;
                item.BlockedReason = "quest-item-missing";
                return;
            }

            state.UnlockedContentIds ??= Array.Empty<string>();
            if (Array.IndexOf(state.UnlockedContentIds, info.UnlockContentId) < 0)
            {
                state.UnlockedContentIds = state.UnlockedContentIds.Append(info.UnlockContentId).ToArray();
            }

            // 技术数据：生产型事务，Commit 那一刻才真正 +N（同 PrimitiveCraftStation 拆解分支模式）。
            string txId = item.QueueItemId + ":techdata";
            CampaignEconomyLedger.ProposeProduce(state, txId, item.QueueItemId, CampaignEconomyLedger.ResourceTechData, info.TechDataYield);
            CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, txId);
            if (reserve.Success)
            {
                CampaignEconomyLedger.MarkRunning(state, txId);
            }
            CampaignEconomyLedger.Commit(state, txId);

            item.State = AnalysisQueueState.Completed;
            item.BlockedReason = null;
            Log.Info($"[HomeValleyAnalysis] {info.DisplayName} 解析完成：解锁 {info.UnlockContentId}，技术数据 +{info.TechDataYield}。");
        }
    }
}
