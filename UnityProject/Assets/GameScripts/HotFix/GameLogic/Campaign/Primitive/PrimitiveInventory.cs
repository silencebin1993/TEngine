using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign.Primitive
{
    /// <summary>ER4-PRIM-03 STORY-EXECUTION-CARDS.md 第1条：战役唯一 <c>PrimitiveInventory</c>——
    /// <see cref="CampaignState.PrimitiveChips"/>（<see cref="PrimitiveChipRecord"/> 数组）的唯一写
    /// 入口，包装既有 <c>GameLogic.MetabolicSlice.Bag.BagInventory</c>/<c>PartInstance</c>/
    /// <c>TransferService</c> 表达的"囊/装卸"语义，但改用战役存档层的 <see cref="PrimitiveChipRecord"/>
    /// 落盘（后者只读属性无法被 <c>JsonUtility</c> 序列化）。
    ///
    /// 三态恰一：<see cref="PrimitiveChipState.Bag"/>（在仓）/<see cref="PrimitiveChipState.Draft"/>
    /// （装在某蓝图电路草稿的 1～7 号槽）/<see cref="PrimitiveChipState.Pending"/>（仓满时的待领取
    /// 队列）——任一实例任一时刻恰好处于其中之一，本类的每个写方法都保证转移是原子的（要么完整转移
    /// 状态+归属，要么整体失败不留下半个中间态）。</summary>
    public static class PrimitiveInventory
    {
        public const int Capacity = 8;

        /// <summary>Demo 唯一开局基础芯片——CardCatalog "organ_focus"="聚焦镜"
        /// （PRIMITIVE-FULL-DEMO-SPEC.md §4.1 点名的两镜之一）。</summary>
        public const string DefaultChipContentId = "organ_focus";

        public const int PrintScrapCost = 5;

        /// <summary>可从"解析/补印"渠道生成基元原型的内容 id 白名单——STORY-EXECUTION-CARDS.md 第1条
        /// "静默标记器/协议/重炮仍只解锁原定组件/固件，不被误转芯片"的具体落点：不在这张白名单里的内容
        /// 一律拒绝生成基元芯片实例，不管调用方传了什么 id。"organ_focus_plus"（精校镜）是 ER4-PRIM-04
        /// 的合成台产物，不通过解析/补印获得，故意不在此列——由合成台调用 <see cref="GrantCrafted"/>
        /// 单独写入。</summary>
        private static readonly HashSet<string> PrimitiveChipSourceIds = new HashSet<string> { DefaultChipContentId };

        public static bool IsPrimitiveChipSource(string contentId) =>
            !string.IsNullOrEmpty(contentId) && PrimitiveChipSourceIds.Contains(contentId);

        private static string NewPartId() => "pchip_" + Guid.NewGuid().ToString("N");

        // ── 只读查询 ─────────────────────────────────────────────────────────────

        public static int BagCount(CampaignState state) =>
            state?.PrimitiveChips?.Count(p => p.State == PrimitiveChipState.Bag) ?? 0;

        public static IReadOnlyList<PrimitiveChipRecord> BagItems(CampaignState state) =>
            (state?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            .Where(p => p.State == PrimitiveChipState.Bag).ToList();

        public static IReadOnlyList<PrimitiveChipRecord> PendingItems(CampaignState state) =>
            (state?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            .Where(p => p.State == PrimitiveChipState.Pending).ToList();

        public static PrimitiveChipRecord Find(CampaignState state, string partId) =>
            state?.PrimitiveChips?.FirstOrDefault(p => p.PartId == partId);

        // ── 播种 ─────────────────────────────────────────────────────────────────

        /// <summary>幂等；供 <see cref="Regions.HomeValleyController.Enter"/> 尾部调用（同
        /// <see cref="Blueprint.BlueprintCircuitDefaults.EnsureCircuitDataSeeded"/> 先例）。只在
        /// <see cref="CampaignState.PrimitiveChips"/> 真正为空（数组长度0，代表"从未播种过"）时才生成
        /// 开局 8 格仓+1 件聚焦镜；已播种过（哪怕玩家后来把仓清空到 0 件）绝不重新生成，避免读档/重复
        /// 调用凭空补货。</summary>
        public static void EnsureSeeded(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            state.PrimitiveChips ??= Array.Empty<PrimitiveChipRecord>();
            if (state.PrimitiveChips.Length > 0)
            {
                return;
            }
            var seed = new PrimitiveChipRecord
            {
                PartId = NewPartId(),
                CardDefId = DefaultChipContentId,
                State = PrimitiveChipState.Bag,
                DraftSlot = -1,
            };
            state.PrimitiveChips = new[] { seed };
        }

        // ── 解析发放 / 补印 ──────────────────────────────────────────────────────

        /// <summary>解析（远征战利品）产出基元芯片的唯一入口。同一 <paramref name="salvageInstanceId"/>
        /// 只生成一次（幂等重放安全，AC-PRM-005"同一 salvageInstanceId 只生成一次"）；仓满时生成的实例
        /// 进入 <see cref="PrimitiveChipState.Pending"/> 待领取队列而不是拒绝/丢弃（同一 AC 原文"仓满不
        /// 软锁、不静默丢件"）。真实解析触发源（区域出征结算）尚未落地（属 ER5/ER6-REGION-01+
        /// ER6-ANA-01 范围），本方法结构完整、可独立测试，供那些 Story 落地后直接调用，不需要改签名——
        /// 与 ER3-STO-01 `TryCollectWreckageDrop` 先于真实触发源落地的处理方式一致。</summary>
        public static CircuitOpResult TryGrantFromSalvage(CampaignState state, string salvageInstanceId, string contentId)
        {
            if (state == null)
            {
                return CircuitOpResult.Fail("no-campaign", "没有活动战役。");
            }
            if (string.IsNullOrEmpty(salvageInstanceId))
            {
                return CircuitOpResult.Fail("invalid-salvage-id", "缺少解析来源实例 ID。");
            }
            if (!IsPrimitiveChipSource(contentId))
            {
                return CircuitOpResult.Fail("not-chip-source",
                    $"'{contentId}' 不是可转化为基元芯片的内容（主线组件/固件不会被误转成芯片）。");
            }

            state.PrimitiveChips ??= Array.Empty<PrimitiveChipRecord>();
            if (state.PrimitiveChips.Any(p => p.SourceSalvageId == salvageInstanceId))
            {
                return CircuitOpResult.Ok(); // 幂等：同一来源已发放过，不重复生成第二个实例
            }

            var record = new PrimitiveChipRecord
            {
                PartId = NewPartId(),
                CardDefId = contentId,
                SourceSalvageId = salvageInstanceId,
                DraftSlot = -1,
                State = BagCount(state) < Capacity ? PrimitiveChipState.Bag : PrimitiveChipState.Pending,
            };
            state.PrimitiveChips = state.PrimitiveChips.Append(record).ToArray();
            AppendLedger(state, "PrimitiveChipGrant", record.PartId, contentId);
            return CircuitOpResult.Ok();
        }

        /// <summary>供电装配站补印基础聚焦镜，5 废料/件。缺电/缺料/仓满/未解锁均在扣款前拒绝且零扣款
        /// （AC-PRM-007"零扣款"），任一失败码互斥、不静默吞失败。</summary>
        public static CircuitOpResult TryPrintChip(CampaignState state, string contentId)
        {
            if (state == null)
            {
                return CircuitOpResult.Fail("no-campaign", "没有活动战役。");
            }
            bool unlocked = contentId == DefaultChipContentId
                || (state.UnlockedContentIds?.Contains(contentId) ?? false);
            if (!unlocked)
            {
                return CircuitOpResult.Fail("not-unlocked", $"'{contentId}' 尚未解锁，不能补印。");
            }
            if (BagCount(state) >= Capacity)
            {
                return CircuitOpResult.Fail("bag-full", "基元仓已满，无法补印新芯片（请先腾格或领取待领取实例）。");
            }

            BuildingRecord station = state.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation);
            bool stationReady = station != null
                && station.ConstructionState == BuildingConstructionState.Operational
                && station.PowerState == BuildingPowerState.Powered;
            if (!stationReady)
            {
                return CircuitOpResult.Fail("no-power", "装配站未供电或未完工，无法补印。");
            }

            string txId = "primitive-print:" + Guid.NewGuid().ToString("N");
            CampaignEconomyLedger.LedgerResult propose = CampaignEconomyLedger.ProposeConsume(
                state, txId, "PrimitiveInventory", CampaignEconomyLedger.ResourceScrap, PrintScrapCost);
            if (!propose.Success)
            {
                return CircuitOpResult.Fail("propose-failed", propose.FailureReason);
            }
            CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, txId);
            if (!reserve.Success)
            {
                CampaignEconomyLedger.Fail(state, txId, reserve.FailureReason);
                return CircuitOpResult.Fail("insufficient-scrap", "废料不足，无法补印。");
            }
            CampaignEconomyLedger.Commit(state, txId);

            var record = new PrimitiveChipRecord
            {
                PartId = NewPartId(),
                CardDefId = contentId,
                State = PrimitiveChipState.Bag,
                DraftSlot = -1,
            };
            state.PrimitiveChips = (state.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Append(record).ToArray();
            AppendLedger(state, "PrimitiveChipPrint", record.PartId, contentId);
            return CircuitOpResult.Ok();
        }

        /// <summary>把 <see cref="PrimitiveChipState.Pending"/> 实例领取进仓（需要有空位）。</summary>
        public static CircuitOpResult TryClaimPending(CampaignState state, string partId)
        {
            PrimitiveChipRecord record = Find(state, partId);
            if (record == null)
            {
                return CircuitOpResult.Fail("part-not-found", $"找不到实例 '{partId}'。");
            }
            if (record.State != PrimitiveChipState.Pending)
            {
                return CircuitOpResult.Fail("not-pending", "该实例不在待领取队列中。");
            }
            // Pending 实例结构上不可能被合成台预留（预留只发生在 Bag 态实例上，见
            // TryReserveForCraft），此处不需要额外校验 ReservedByTransactionId。
            if (BagCount(state) >= Capacity)
            {
                return CircuitOpResult.Fail("bag-full", "基元仓已满，无法领取，请先腾格。");
            }
            record.State = PrimitiveChipState.Bag;
            return CircuitOpResult.Ok();
        }

        // ── 装/卸/跨草稿 ─────────────────────────────────────────────────────────

        /// <summary>把仓内实例装入指定蓝图草稿的指定槽——先落 <paramref name="board"/>.TryPlaceChip
        /// 校验合法性（槽类型/是否已解锁/是否非法内容全部复用既有 <see cref="BlueprintCircuitBoard"/>
        /// 规则，本方法不重复实现），成功后才把实例转 Draft；board 拒绝则整体失败、仓状态不变
        /// （原子性——不会出现"实例已转 Draft 但槽位其实没装上"的中间态）。</summary>
        public static CircuitOpResult TryMoveToDraft(CampaignState state, BlueprintCircuitBoard board,
            string blueprintId, int slot, string partId)
        {
            if (state == null || board == null)
            {
                return CircuitOpResult.Fail("no-context", "没有活动战役或电路草稿。");
            }
            PrimitiveChipRecord record = Find(state, partId);
            if (record == null)
            {
                return CircuitOpResult.Fail("part-not-found", $"找不到实例 '{partId}'。");
            }
            if (record.State != PrimitiveChipState.Bag)
            {
                return CircuitOpResult.Fail("not-in-bag", "该实例不在仓中（可能已装在别处或待领取）。");
            }
            if (!string.IsNullOrEmpty(record.ReservedByTransactionId))
            {
                // ER4-PRIM-04：该实例已被合成台预留为材料，装/卸/领取三条既有通道必须统一拒绝
                // （PRIMITIVE-FULL-DEMO-SPEC.md §4.2"未被其他事务预留……UI 显示已预留/不可装拆"）。
                return CircuitOpResult.Fail("reserved-for-craft", "该实例已被合成台预留为材料，暂不可装卸。");
            }

            CircuitOpResult placed = board.TryPlaceChip(slot, record.CardDefId);
            if (!placed.Success)
            {
                return placed;
            }

            record.State = PrimitiveChipState.Draft;
            record.DraftBlueprintId = blueprintId;
            record.DraftSlot = slot;
            board.SlotPartIds[slot] = partId;
            return CircuitOpResult.Ok();
        }

        /// <summary>把某槽当前占用的实例卸回仓（不管它属于哪个蓝图）。AC-PRM-006 明文"仓满卸下拒绝且
        /// 保留原槽"——仓容量 8 是硬上限，卸回是把一个 Draft 态实例真正转成占一个仓格的 Bag 态实例，
        /// 仓已经满时physically 没有格子可放，必须在改动 <paramref name="board"/> 之前就拒绝，原槽内容
        /// 保持不动（不是先卸下再发现放不进去那种半途而废的失败）。只有该槽确实绑定着一个仓内实例
        /// （<c>board.SlotPartIds[slot]</c> 非空）才受这条容量闸门约束——非实例背书的板面内容（如测试
        /// 直接调用 <c>board.TryPlaceChip</c> 写入、没有对应仓实例）走普通移除，不涉及仓容量。</summary>
        public static CircuitOpResult TryMoveToBag(CampaignState state, BlueprintCircuitBoard board, int slot)
        {
            if (state == null || board == null)
            {
                return CircuitOpResult.Fail("no-context", "没有活动战役或电路草稿。");
            }
            string partId = (slot >= 0 && slot < board.SlotPartIds.Length) ? board.SlotPartIds[slot] : null;

            if (!string.IsNullOrEmpty(partId) && BagCount(state) >= Capacity)
            {
                return CircuitOpResult.Fail("bag-full", "基元仓已满，无法卸回，请先腾格（该实例保留在原槽）。");
            }

            CircuitOpResult removed = board.TryRemoveChip(slot);
            if (!removed.Success)
            {
                return removed;
            }

            if (!string.IsNullOrEmpty(partId))
            {
                PrimitiveChipRecord record = Find(state, partId);
                if (record != null)
                {
                    record.State = PrimitiveChipState.Bag;
                    record.DraftBlueprintId = null;
                    record.DraftSlot = -1;
                }
            }
            return CircuitOpResult.Ok();
        }

        /// <summary>切换正在编辑的蓝图草稿（或关闭面板）前调用：把仍标记为"装在
        /// <paramref name="blueprintId"/>"但不在 <paramref name="savedSlotPartIds"/>（该蓝图最后一次
        /// 真正保存的 <c>BlueprintVersionRecord.CircuitSlotPartIds</c>）里的实例释放回仓——对应
        /// "编辑的只是草稿，未保存的改动离开编辑会话时应该撤销"这条既有 ER4-PRIM-02 哲学在实例层面的
        /// 延伸：草稿里临时装上又没保存的芯片不该继续占着"已装备"状态却又查无所属。<paramref
        /// name="savedSlotPartIds"/> 传 null（该蓝图从未真正保存过电路数据，如骨架/尚未播种）时视为
        /// "没有任何已保存实例"，本方法会把该蓝图名下全部 Draft 实例释放。幂等：重复调用不产生副作用。</summary>
        public static void ReconcileBlueprintDrafts(CampaignState state, string blueprintId, string[] savedSlotPartIds)
        {
            if (state?.PrimitiveChips == null || string.IsNullOrEmpty(blueprintId))
            {
                return;
            }
            var savedSet = new HashSet<string>((savedSlotPartIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrEmpty(id)));

            foreach (PrimitiveChipRecord record in state.PrimitiveChips)
            {
                if (record.State != PrimitiveChipState.Draft || record.DraftBlueprintId != blueprintId)
                {
                    continue;
                }
                if (savedSet.Contains(record.PartId))
                {
                    continue; // 这是已保存版本真实持有的实例，保留 Draft 态。
                }
                record.State = PrimitiveChipState.Bag;
                record.DraftBlueprintId = null;
                record.DraftSlot = -1;
            }
        }

        // ── ER4-PRIM-04：合成台材料预留 ──────────────────────────────────────────

        /// <summary>把一个 Bag 态实例预留给某个合成/拆解事务（<paramref name="transactionId"/> 即
        /// <c>CraftQueueItemRecord.QueueItemId</c>）。预留期间实例仍是 Bag 态、仍占仓格（"仓占用暂不
        /// 下降"），但 <see cref="TryMoveToDraft"/> 会拒绝对它的装卸——由 <c>PrimitiveCraftStation</c>
        /// 在入队时对每个材料调用一次，两个材料任一预留失败则整体回滚（调用方负责，本方法本身单实例
        /// 原子）。</summary>
        public static CircuitOpResult TryReserveForCraft(CampaignState state, string partId, string transactionId)
        {
            PrimitiveChipRecord record = Find(state, partId);
            if (record == null)
            {
                return CircuitOpResult.Fail("part-not-found", $"找不到实例 '{partId}'。");
            }
            if (record.State != PrimitiveChipState.Bag)
            {
                return CircuitOpResult.Fail("not-in-bag", "材料必须在仓中（如在草稿槽，请先卸回仓）。");
            }
            if (!string.IsNullOrEmpty(record.ReservedByTransactionId) && record.ReservedByTransactionId != transactionId)
            {
                return CircuitOpResult.Fail("already-reserved", "该实例已被另一个合成/拆解事务预留。");
            }
            record.ReservedByTransactionId = transactionId;
            return CircuitOpResult.Ok();
        }

        /// <summary>取消/失败时释放材料预留（实例本身不受影响，仍在仓中）。找不到实例或未被该事务预留
        /// 均安全 no-op——供 <c>PrimitiveCraftStation.TryCancel</c> 无条件调用，不必先查状态。</summary>
        public static void ReleaseCraftReservation(CampaignState state, string partId, string transactionId)
        {
            PrimitiveChipRecord record = Find(state, partId);
            if (record != null && record.ReservedByTransactionId == transactionId)
            {
                record.ReservedByTransactionId = null;
            }
        }

        /// <summary>合成/拆解真正提交时消耗一件材料——实例整体从 <see cref="CampaignState.PrimitiveChips"/>
        /// 移除（不是转到别的 State；材料被真正消耗掉，不再是"某处的一个实例"）。只允许消耗仍处于 Bag
        /// 态且确实被该事务预留的实例，防止提交时序错乱下误删无关实例。</summary>
        public static CircuitOpResult ConsumeReservedMaterial(CampaignState state, string partId, string transactionId)
        {
            PrimitiveChipRecord record = Find(state, partId);
            if (record == null)
            {
                return CircuitOpResult.Fail("part-not-found", $"找不到实例 '{partId}'（合成材料在提交前消失）。");
            }
            if (record.State != PrimitiveChipState.Bag || record.ReservedByTransactionId != transactionId)
            {
                return CircuitOpResult.Fail("material-mismatch", $"实例 '{partId}' 状态与预留不一致，无法消耗。");
            }
            state.PrimitiveChips = state.PrimitiveChips.Where(p => p.PartId != partId).ToArray();
            return CircuitOpResult.Ok();
        }

        // ── 事件账本（审计用，不是核心正确性依据——核心依据是 SourceSalvageId 去重与状态机本身）──

        private static void AppendLedger(CampaignState state, string category, string partId, string contentId)
        {
            state.EventLedger ??= Array.Empty<EventLedgerEntry>();
            var entry = new EventLedgerEntry
            {
                EventId = category + ":" + partId,
                Category = category,
                GrantedAtPlaySeconds = state.PlaySeconds,
                Payload = contentId,
            };
            if (state.EventLedger.Any(e => e.EventId == entry.EventId))
            {
                return; // 幂等：同一实例同一类事件只记一条
            }
            state.EventLedger = state.EventLedger.Append(entry).ToArray();
        }

        /// <summary>ER4-PRIM-04：合成台升级配方产物（"精校聚焦镜"等）直接进仓的唯一入口，不经解析/
        /// 补印两条既有渠道（后两者都要求内容在 <see cref="PrimitiveChipSourceIds"/> 白名单里，合成
        /// 产物故意不在白名单——只有合成台自己能创造它）。仓满时自动落 Pending，行为与
        /// <see cref="TryGrantFromSalvage"/>/<see cref="TryPrintChip"/> 一致，返回新实例的 PartId 供
        /// 调用方（<c>PrimitiveCraftStation</c>）写入 <c>CraftQueueItemRecord.OutputPartId</c>。</summary>
        public static string GrantCrafted(CampaignState state, string contentId)
        {
            if (state == null)
            {
                return null;
            }
            var record = new PrimitiveChipRecord
            {
                PartId = NewPartId(),
                CardDefId = contentId,
                DraftSlot = -1,
                State = BagCount(state) < Capacity ? PrimitiveChipState.Bag : PrimitiveChipState.Pending,
            };
            state.PrimitiveChips = (state.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Append(record).ToArray();
            AppendLedger(state, "PrimitiveChipCraft", record.PartId, contentId);
            return record.PartId;
        }
    }
}
