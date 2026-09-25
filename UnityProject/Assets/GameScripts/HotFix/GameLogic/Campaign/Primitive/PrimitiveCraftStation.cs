using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign.Primitive
{
    /// <summary>ER4-PRIM-04 STORY-EXECUTION-CARDS.md：合成台（PRIMITIVE-FULL-DEMO-SPEC.md §4.2
    /// "电路合成台"）唯一队列状态机——<see cref="CraftQueueItemRecord"/> 的唯一写入口。结构镜像
    /// <see cref="HomeValleyFactory"/>（单 Running 工位 FIFO、按 <c>CreatedTick</c> 排序、断电转
    /// WaitingPower、入队只 Propose 不扣款、成为队首才 Reserve），复用同一栋装配站
    /// （<see cref="HomeValleyLayout.BuildingTypeAssemblyStation"/>）判定供电——DEMO-CONTENT-LOCK.md
    /// 归还谷地建筑清单里没有独立的"合成台"物理建筑，补印基础聚焦镜同样吃装配站供电
    /// （见 <see cref="PrimitiveInventory.TryPrintChip"/>），"电路合成台"是同一栋建筑上的第三种可操作
    /// 功能（生产机器/回厂改造/合成材料），不是新增建筑类型。
    ///
    /// 两个最小配方（PRIMITIVE-FULL-DEMO-SPEC.md §4.1 第5条）：
    /// <list type="bullet">
    /// <item>Upgrade：两件 <see cref="PrimitiveInventory.DefaultChipContentId"/>（聚焦镜）+ 5 废料 + 8 秒
    /// → 一件 <see cref="UpgradeOutputContentId"/>（精校聚焦镜）。</item>
    /// <item>Disassemble：任意一件仓内芯片 + 3 秒 → 2 废料（固定返还，不消耗废料）。仓内实例结构上永远
    /// 不可能是 0/8 号固定源汇（那两个槽从不经 <see cref="PrimitiveInventory"/> 分配实例），"不允许拆
    /// 起始电源/主武器锚点"这条约束因此是结构性自动满足，不需要额外校验。</item>
    /// </list>
    ///
    /// 材料锁定不经 <see cref="CampaignEconomyLedger"/>（后者只管 Scrap/TechData/PowerCapacity 三顶层
    /// 资源，不管物品实例）——材料的"预留/提交/释放"由 <see cref="PrimitiveInventory.TryReserveForCraft"/>/
    /// <see cref="PrimitiveInventory.ConsumeReservedMaterial"/>/<see cref="PrimitiveInventory.ReleaseCraftReservation"/>
    /// 三个方法独立承载，与废料侧事务（<see cref="CraftQueueItemRecord.TransactionId"/>）并行但分开管理，
    /// 二者必须在 Committing 那一刻同一次调用里同时成功或同时失败（本类 <see cref="CompleteCraft"/>
    /// 保证）。</summary>
    public static class PrimitiveCraftStation
    {
        public const string UpgradeOutputContentId = "organ_focus_plus";
        public const int UpgradeScrapCost = 5;
        public const float UpgradeDuration = 8f;
        public const float DisassembleDuration = 3f;
        public const int DisassembleScrapYield = 2;

        /// <summary>"一 Running 工位/最多八等待"——总活跃（非终态）队列项上限 = 1 运行中 + 8 等待。</summary>
        public const int MaxActiveQueueItems = 9;

        public readonly struct CraftOpResult
        {
            public readonly bool Success;
            public readonly string FailureReason;
            public readonly string QueueItemId;

            private CraftOpResult(bool success, string failureReason, string queueItemId)
            {
                Success = success;
                FailureReason = failureReason;
                QueueItemId = queueItemId;
            }

            public static CraftOpResult Ok(string queueItemId) => new CraftOpResult(true, null, queueItemId);
            public static CraftOpResult Fail(string reason) => new CraftOpResult(false, reason, null);
        }

        private static readonly CraftQueueState[] CancellableStates =
        {
            CraftQueueState.Queued, CraftQueueState.WaitingResources,
            CraftQueueState.WaitingPower, CraftQueueState.Running,
        };

        private static bool IsCancellable(CraftQueueState s) => CancellableStates.Contains(s);

        private static bool IsHeadCandidate(CraftQueueState s) =>
            s == CraftQueueState.Queued || s == CraftQueueState.WaitingResources
            || s == CraftQueueState.WaitingPower || s == CraftQueueState.Running;

        public static CraftQueueItemRecord Find(CampaignState state, string queueItemId) =>
            state?.CraftQueues?.FirstOrDefault(q => q.QueueItemId == queueItemId);

        private static long NowTick(CampaignState state) => (long)(state.PlaySeconds * 1000f);

        private static int ActiveCount(CampaignState state) =>
            state.CraftQueues?.Count(q => IsHeadCandidate(q.State)) ?? 0;

        private static void Append(CampaignState state, CraftQueueItemRecord item)
        {
            state.CraftQueues = (state.CraftQueues ?? Array.Empty<CraftQueueItemRecord>()).Append(item).ToArray();
        }

        // ── 入队 ─────────────────────────────────────────────────────────────────

        /// <summary>升级：两件不同的仓内聚焦镜实例 + 5 废料 → 一件精校聚焦镜。"确认时重验两实例不同且
        /// 在仓中……原子登记两个 reservedByTransactionId 与 5 废料预留"——两个材料预留与一次废料
        /// Propose 必须同时成功，任一步失败则整体回滚（已预留的材料释放、已创建的事务/队列项撤销），
        /// 不留半成品状态。</summary>
        public static CraftOpResult TryEnqueueUpgrade(CampaignState state, string partIdA, string partIdB)
        {
            if (state == null || string.IsNullOrEmpty(partIdA) || string.IsNullOrEmpty(partIdB))
            {
                return CraftOpResult.Fail("invalid-args");
            }
            if (partIdA == partIdB)
            {
                return CraftOpResult.Fail("materials-not-distinct");
            }
            PrimitiveChipRecord recA = PrimitiveInventory.Find(state, partIdA);
            PrimitiveChipRecord recB = PrimitiveInventory.Find(state, partIdB);
            if (recA == null || recB == null)
            {
                return CraftOpResult.Fail("part-not-found");
            }
            if (recA.CardDefId != PrimitiveInventory.DefaultChipContentId || recB.CardDefId != PrimitiveInventory.DefaultChipContentId)
            {
                return CraftOpResult.Fail("wrong-material-content");
            }
            if (ActiveCount(state) >= MaxActiveQueueItems)
            {
                return CraftOpResult.Fail("queue-full");
            }

            string queueItemId = "craft:upgrade:" + Guid.NewGuid().ToString("N").Substring(0, 8);

            CircuitOpResult reserveA = PrimitiveInventory.TryReserveForCraft(state, partIdA, queueItemId);
            if (!reserveA.Success)
            {
                return CraftOpResult.Fail(reserveA.Code);
            }
            CircuitOpResult reserveB = PrimitiveInventory.TryReserveForCraft(state, partIdB, queueItemId);
            if (!reserveB.Success)
            {
                PrimitiveInventory.ReleaseCraftReservation(state, partIdA, queueItemId); // 回滚已预留的第一件
                return CraftOpResult.Fail(reserveB.Code);
            }

            string txId = queueItemId + ":tx";
            CampaignEconomyLedger.ProposeConsume(state, txId, queueItemId, CampaignEconomyLedger.ResourceScrap, UpgradeScrapCost);

            var item = new CraftQueueItemRecord
            {
                QueueItemId = queueItemId,
                Kind = CraftQueueKind.Upgrade,
                MaterialPartIds = new[] { partIdA, partIdB },
                TransactionId = txId,
                Duration = UpgradeDuration,
                Progress = 0f,
                State = CraftQueueState.Queued,
                CreatedTick = NowTick(state),
            };
            Append(state, item);
            return CraftOpResult.Ok(queueItemId);
        }

        /// <summary>拆解：任意一件仓内芯片 + 3 秒 → 2 废料（无废料输入，产出用生产型事务承载）。</summary>
        public static CraftOpResult TryEnqueueDisassemble(CampaignState state, string partId)
        {
            if (state == null || string.IsNullOrEmpty(partId))
            {
                return CraftOpResult.Fail("invalid-args");
            }
            PrimitiveChipRecord rec = PrimitiveInventory.Find(state, partId);
            if (rec == null)
            {
                return CraftOpResult.Fail("part-not-found");
            }
            if (ActiveCount(state) >= MaxActiveQueueItems)
            {
                return CraftOpResult.Fail("queue-full");
            }

            string queueItemId = "craft:disassemble:" + Guid.NewGuid().ToString("N").Substring(0, 8);

            CircuitOpResult reserve = PrimitiveInventory.TryReserveForCraft(state, partId, queueItemId);
            if (!reserve.Success)
            {
                return CraftOpResult.Fail(reserve.Code);
            }

            string txId = queueItemId + ":tx";
            CampaignEconomyLedger.ProposeProduce(state, txId, queueItemId, CampaignEconomyLedger.ResourceScrap, DisassembleScrapYield);

            var item = new CraftQueueItemRecord
            {
                QueueItemId = queueItemId,
                Kind = CraftQueueKind.Disassemble,
                MaterialPartIds = new[] { partId, null },
                TransactionId = txId,
                Duration = DisassembleDuration,
                Progress = 0f,
                State = CraftQueueState.Queued,
                CreatedTick = NowTick(state),
            };
            Append(state, item);
            return CraftOpResult.Ok(queueItemId);
        }

        // ── 取消 ─────────────────────────────────────────────────────────────────

        /// <summary>"Queued/Running/WaitingPower 取消均完整释放材料锁与废料预留"——材料只是解除预留
        /// （回到普通 Bag 态，不删除实例），废料事务走 <see cref="CampaignEconomyLedger.Cancel"/>
        /// 全额退款（消费型 Reserve 已扣的 5 废料退回；生产型 Cancel 是空退款，本就没扣）。</summary>
        public static CraftOpResult TryCancel(CampaignState state, string queueItemId)
        {
            CraftQueueItemRecord item = Find(state, queueItemId);
            if (item == null)
            {
                return CraftOpResult.Fail($"not-found:{queueItemId}");
            }
            if (!IsCancellable(item.State))
            {
                return CraftOpResult.Fail($"cannot-cancel-from:{item.State}");
            }

            foreach (string partId in item.MaterialPartIds)
            {
                if (!string.IsNullOrEmpty(partId))
                {
                    PrimitiveInventory.ReleaseCraftReservation(state, partId, queueItemId);
                }
            }
            if (!string.IsNullOrEmpty(item.TransactionId))
            {
                CampaignEconomyLedger.Cancel(state, item.TransactionId);
            }
            item.State = CraftQueueState.Cancelled;
            item.BlockedReason = null;
            return CraftOpResult.Ok(queueItemId);
        }

        // ── 每帧驱动 ─────────────────────────────────────────────────────────────

        /// <summary>由 <see cref="HomeValleyController.Update"/> 每帧调用一次（同
        /// <see cref="HomeValleyFactory.Tick"/> 性能纪律：队列量级恒定个位数）。</summary>
        public static void Tick(CampaignState state, float dt)
        {
            if (state == null || state.CraftQueues == null || state.CraftQueues.Length == 0)
            {
                return;
            }

            BuildingRecord station = state.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation);
            bool stationDestroyed = station == null || station.ConstructionState == BuildingConstructionState.Destroyed;
            if (stationDestroyed)
            {
                foreach (CraftQueueItemRecord it in state.CraftQueues)
                {
                    if (!IsCancellable(it.State))
                    {
                        continue;
                    }
                    foreach (string partId in it.MaterialPartIds)
                    {
                        if (!string.IsNullOrEmpty(partId))
                        {
                            PrimitiveInventory.ReleaseCraftReservation(state, partId, it.QueueItemId);
                        }
                    }
                    if (!string.IsNullOrEmpty(it.TransactionId))
                    {
                        CampaignEconomyLedger.Cancel(state, it.TransactionId);
                    }
                    it.State = CraftQueueState.Failed;
                    it.BlockedReason = "assembly-station-destroyed";
                }
                return;
            }

            CraftQueueItemRecord head = FindHead(state);
            if (head != null)
            {
                TickHead(state, head, dt, station);
            }

            // OutputWaiting：产物在待领取队列里被玩家真正领走（PrimitiveInventory.TryClaimPending）后，
            // 这里逐帧确认并把队列项本身也收尾成 Completed——不影响"只可领取一次"（领取动作本身的幂等性
            // 由 PrimitiveChipRecord.State 的 Pending→Bag 单向转换保证，这里只是同步队列项的展示状态）。
            foreach (CraftQueueItemRecord it in state.CraftQueues)
            {
                if (it.State != CraftQueueState.OutputWaiting)
                {
                    continue;
                }
                PrimitiveChipRecord output = PrimitiveInventory.Find(state, it.OutputPartId);
                if (output != null && output.State == PrimitiveChipState.Bag)
                {
                    it.State = CraftQueueState.Completed;
                }
            }
        }

        private static CraftQueueItemRecord FindHead(CampaignState state)
        {
            CraftQueueItemRecord head = null;
            foreach (CraftQueueItemRecord it in state.CraftQueues)
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

        private static void TickHead(CampaignState state, CraftQueueItemRecord item, float dt, BuildingRecord station)
        {
            bool powered = station.ConstructionState == BuildingConstructionState.Operational
                && station.PowerState == BuildingPowerState.Powered;

            if (item.State == CraftQueueState.Running)
            {
                if (!powered)
                {
                    item.State = CraftQueueState.WaitingPower;
                    item.BlockedReason = "assembly-station-unpowered";
                    return;
                }
                item.Progress += dt;
                if (item.Progress >= item.Duration)
                {
                    CompleteCraft(state, item);
                }
                return;
            }

            // Queued / WaitingResources / WaitingPower：逐帧尝试开工。
            if (!powered)
            {
                item.State = CraftQueueState.WaitingPower;
                item.BlockedReason = "assembly-station-unpowered";
                return;
            }

            CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, item.TransactionId);
            if (!reserve.Success)
            {
                item.State = CraftQueueState.WaitingResources;
                item.BlockedReason = reserve.FailureReason;
                return;
            }

            CampaignEconomyLedger.MarkRunning(state, item.TransactionId);
            item.State = CraftQueueState.Running;
            item.BlockedReason = null;
        }

        /// <summary>Committing：到时二次核验材料仍在、仍被本事务预留，一次性原子提交。任一材料在此刻
        /// 缺失/被挪用（"两件材料中一个消失"场景，STORY-EXECUTION-CARDS.md 明确要测）——转 Failed，
        /// 释放仍存在的材料预留与废料预留，不凭空产出、不留半成品。</summary>
        private static void CompleteCraft(CampaignState state, CraftQueueItemRecord item)
        {
            item.Progress = item.Duration; // 钳制，不倒退不越界。

            foreach (string partId in item.MaterialPartIds)
            {
                if (string.IsNullOrEmpty(partId))
                {
                    continue;
                }
                PrimitiveChipRecord rec = PrimitiveInventory.Find(state, partId);
                bool intact = rec != null && rec.State == PrimitiveChipState.Bag && rec.ReservedByTransactionId == item.QueueItemId;
                if (!intact)
                {
                    FailCraft(state, item, "material-missing");
                    return;
                }
            }

            if (item.Kind == CraftQueueKind.Upgrade)
            {
                CircuitOpResult consumeA = PrimitiveInventory.ConsumeReservedMaterial(state, item.MaterialPartIds[0], item.QueueItemId);
                CircuitOpResult consumeB = PrimitiveInventory.ConsumeReservedMaterial(state, item.MaterialPartIds[1], item.QueueItemId);
                if (!consumeA.Success || !consumeB.Success)
                {
                    FailCraft(state, item, "material-missing");
                    return;
                }
                CampaignEconomyLedger.Commit(state, item.TransactionId);
                string outputPartId = PrimitiveInventory.GrantCrafted(state, UpgradeOutputContentId);
                item.OutputPartId = outputPartId;
                PrimitiveChipRecord output = PrimitiveInventory.Find(state, outputPartId);
                item.State = (output != null && output.State == PrimitiveChipState.Pending)
                    ? CraftQueueState.OutputWaiting
                    : CraftQueueState.Completed;
                item.BlockedReason = null;
            }
            else // Disassemble
            {
                CircuitOpResult consume = PrimitiveInventory.ConsumeReservedMaterial(state, item.MaterialPartIds[0], item.QueueItemId);
                if (!consume.Success)
                {
                    FailCraft(state, item, "material-missing");
                    return;
                }
                CampaignEconomyLedger.LedgerResult reserveProduce = CampaignEconomyLedger.Reserve(state, item.TransactionId);
                if (reserveProduce.Success)
                {
                    CampaignEconomyLedger.MarkRunning(state, item.TransactionId);
                }
                CampaignEconomyLedger.Commit(state, item.TransactionId); // 生产型：Commit 这一刻才真正 +2 废料。
                item.State = CraftQueueState.Completed;
                item.BlockedReason = null;
            }
        }

        private static void FailCraft(CampaignState state, CraftQueueItemRecord item, string reason)
        {
            foreach (string partId in item.MaterialPartIds)
            {
                if (!string.IsNullOrEmpty(partId))
                {
                    PrimitiveInventory.ReleaseCraftReservation(state, partId, item.QueueItemId);
                }
            }
            if (!string.IsNullOrEmpty(item.TransactionId))
            {
                CampaignEconomyLedger.Fail(state, item.TransactionId, reason);
            }
            item.State = CraftQueueState.Failed;
            item.BlockedReason = reason;
            // ER8-CONTENT-01 AC-AUD-001 失败：reason 是内部原因码，不直接给玩家看。
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Failure, "合成失败，材料已释放");
        }
    }
}
