using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER3-STO-01：ERD-ECO-003 仓储的唯一写入口。归还谷地目前只有两处真正的家园存量
    /// 容器——归还核心应急缓存（恒定 180，见 <see cref="HomeValleyLayout.CoreCacheCapacity"/>）和
    /// 仓库（修复后追加 300，见 <see cref="HomeValleyLayout.WarehouseCapacity"/>）——废料的物理数量
    /// 仍然是既有的 <see cref="CampaignState.Scrap"/> 标量（ER3-ECO-01/ER3-PWR-01 已大量依赖它做
    /// 修复成本结算，本 Story 不推翻重写成逐建筑 <see cref="BuildingRecord.Inventory"/> 条目），
    /// 本类只在其上加一层容量天花板：任何"发放到家园存量"的动作必须先过
    /// <see cref="GetAvailableSpace"/> 检查，通不过就地留在 <see cref="CampaignState.GroundItems"/>
    /// （地面物，第三类独立存放位置，ERD-ECO-003"库存唯一真相属于仓库/核心缓存/机器货舱；地面物是
    /// 独立实体"），不静默丢弃也不超容写入。
    ///
    /// 搬运的"从来源预留、到达目标才提交"用显式两阶段票据（<see cref="HaulTicket"/>）表达：
    /// <see cref="TryReserveHaul"/> 从地面物里取出（预留，物品暂时不在任何容器/地面里）；
    /// <see cref="CommitHaul"/> 成功交付到家园存量；<see cref="DropHaul"/> 在中断（机器阵亡/玩家
    /// 取消/换目标）时把同一份 <see cref="HaulTicket.SalvageInstanceId"/> 放回地面，物品不消失也
    /// 不复制。本 Story 范围内拾取到交付是同一次调用链完成（归还谷地暂无真实运输在途时间——那是
    /// ER3-WRK-01/02 的分配与寻路范围），但两阶段接口本身已经支持后续 Story 在 Reserve 和 Commit
    /// 之间插入真实移动过程，不需要改本类签名。</summary>
    public static class HomeValleyCargo
    {
        public readonly struct StoreResult
        {
            public readonly bool Success;
            public readonly string FailureReason;

            private StoreResult(bool success, string failureReason)
            {
                Success = success;
                FailureReason = failureReason;
            }

            public static StoreResult Ok() => new StoreResult(true, null);
            public static StoreResult Fail(string reason) => new StoreResult(false, reason);
        }

        /// <summary>一次搬运的凭证。纯内存态，不落盘——本 Story 范围内拾取到交付/放下在同一调用链
        /// 完成，不存在"跨帧持有票据"的场景；<see cref="GroundItemRecord"/> 本身才是落盘的持久态。</summary>
        public sealed class HaulTicket
        {
            public string ResourceType;
            public int Amount;
            public string SalvageInstanceId;
            public Vector2 SourcePosition;
        }

        /// <summary>家园存量总容量：核心缓存恒定 <see cref="HomeValleyLayout.CoreCacheCapacity"/>；
        /// 仓库 Operational 时追加 <see cref="HomeValleyLayout.WarehouseCapacity"/>。ERD-ECO-003
        /// "核心缓存不能接收远征战利品"——废料以外的资源类型（模块/数据盒等远征战利品，尚未有真实
        /// 内容，ER4-CONTENT-01 起才会产出）只能计入仓库那一份，核心缓存对它们贡献恒为 0。</summary>
        public static int GetStorageCapacity(CampaignState state, string resourceType)
        {
            bool warehouseOperational = FindBuilding(state, HomeValleyLayout.BuildingTypeWarehouse)?.ConstructionState
                == BuildingConstructionState.Operational;
            int warehousePart = warehouseOperational ? HomeValleyLayout.WarehouseCapacity : 0;

            if (resourceType == CampaignEconomyLedger.ResourceScrap)
            {
                return HomeValleyLayout.CoreCacheCapacity + warehousePart;
            }
            return warehousePart;
        }

        /// <summary>当前家园存量已占用数量。废料就是既有的 <see cref="CampaignState.Scrap"/> 标量；
        /// 其余资源类型本 Story 尚无真实持有量字段（无内容可落），恒为 0。</summary>
        public static int GetStorageUsed(CampaignState state, string resourceType)
        {
            if (resourceType == CampaignEconomyLedger.ResourceScrap)
            {
                return state.Scrap;
            }
            return 0;
        }

        public static int GetAvailableSpace(CampaignState state, string resourceType)
        {
            return Math.Max(0, GetStorageCapacity(state, resourceType) - GetStorageUsed(state, resourceType));
        }

        // ── 地面物 ──────────────────────────────────────────────────────────────

        public static GroundItemRecord FindGroundItem(CampaignState state, string groundItemId)
        {
            return state?.GroundItems?.FirstOrDefault(g => g.GroundItemId == groundItemId);
        }

        public static GroundItemRecord FindGroundItemBySalvageId(CampaignState state, string salvageInstanceId)
        {
            return state?.GroundItems?.FirstOrDefault(g => g.SalvageInstanceId == salvageInstanceId);
        }

        /// <summary>在地面生成一份新物品。幂等：同一 <paramref name="salvageInstanceId"/> 已存在于地面
        /// 时直接返回既有记录，不重复生成第二份（100 次压力测试的重试路径必须依赖这条幂等性，否则
        /// "中断重试"会不断复制出新地面物）。</summary>
        public static GroundItemRecord SpawnGroundItem(CampaignState state, string regionId, Vector2 position,
            string resourceType, int amount, string salvageInstanceId)
        {
            GroundItemRecord existing = FindGroundItemBySalvageId(state, salvageInstanceId);
            if (existing != null)
            {
                return existing;
            }

            var item = new GroundItemRecord
            {
                GroundItemId = "ground:" + salvageInstanceId,
                RegionId = regionId,
                Position = position,
                ResourceType = resourceType,
                Amount = amount,
                SalvageInstanceId = salvageInstanceId,
            };
            state.GroundItems = (state.GroundItems ?? Array.Empty<GroundItemRecord>()).Append(item).ToArray();
            return item;
        }

        private static void RemoveGroundItem(CampaignState state, string groundItemId)
        {
            state.GroundItems = (state.GroundItems ?? Array.Empty<GroundItemRecord>())
                .Where(g => g.GroundItemId != groundItemId)
                .ToArray();
        }

        // ── 搬运两阶段票据 ──────────────────────────────────────────────────────

        /// <summary>阶段一：从地面预留（拾取）。物品从 <see cref="CampaignState.GroundItems"/> 移除，
        /// 在最终 <see cref="CommitHaul"/>/<see cref="DropHaul"/> 之前不存在于任何容器或地面——这正是
        /// "预留期间"的语义，调用方若要展示"机器货舱里有货"可以自行在 <see cref="MachineRecord.Cargo"/>
        /// 上镜像票据信息（本 Story 不强制，归还谷地暂无逐机货舱可视化需求）。</summary>
        public static HaulTicket TryReserveHaul(CampaignState state, string groundItemId)
        {
            GroundItemRecord item = FindGroundItem(state, groundItemId);
            if (item == null)
            {
                return null;
            }

            var ticket = new HaulTicket
            {
                ResourceType = item.ResourceType,
                Amount = item.Amount,
                SalvageInstanceId = item.SalvageInstanceId,
                SourcePosition = item.Position,
            };
            RemoveGroundItem(state, item.GroundItemId);
            return ticket;
        }

        /// <summary>阶段二 a：交付到家园存量。容量不足时**不丢弃**——物品原样放回地面（
        /// <see cref="HaulTicket.SourcePosition"/>，即拾取前的位置）并返回失败，调用方（HUD/工作面板）
        /// 据此显示"仓满 Waiting"，之后容量释放可直接重试同一票据信息，不需要重开场景
        /// （AC-ECO-006）。<paramref name="ledgerTransactionId"/> 非空时，真正的 +Scrap 由
        /// <see cref="CampaignEconomyLedger.Commit"/> 完成（本次交付流的资源事务，保持与 ER3-ECO-01
        /// 既定"生产型事务 Commit 才发放"的唯一写入口不冲突，避免同一笔废料被这里和账本各加一次）；
        /// 为空时（没有配套的资源事务，如未来其他来源的搬运）直接改 <see cref="CampaignState.Scrap"/>。
        /// 目前只支持废料——其余资源类型本 Story 无真实持有量字段，直接拒绝。</summary>
        public static StoreResult CommitHaul(CampaignState state, HaulTicket ticket, string ledgerTransactionId = null)
        {
            if (ticket == null)
            {
                return StoreResult.Fail("invalid-ticket");
            }
            if (ticket.ResourceType != CampaignEconomyLedger.ResourceScrap)
            {
                return StoreResult.Fail($"unsupported-resource-type:{ticket.ResourceType}");
            }

            int available = GetAvailableSpace(state, ticket.ResourceType);
            if (available < ticket.Amount)
            {
                SpawnGroundItem(state, HomeValleyLayout.RegionId, ticket.SourcePosition,
                    ticket.ResourceType, ticket.Amount, ticket.SalvageInstanceId);
                return StoreResult.Fail($"storage-full:need={ticket.Amount}:have={available}");
            }

            if (!string.IsNullOrEmpty(ledgerTransactionId))
            {
                CampaignEconomyLedger.Commit(state, ledgerTransactionId);
            }
            else
            {
                state.Scrap += ticket.Amount;
            }
            return StoreResult.Ok();
        }

        /// <summary>阶段二 b：中断（机器阵亡/玩家取消/换目标）。物品落在
        /// <paramref name="dropPosition"/>（调用方传入的"最后合法位置"——机器阵亡传死亡坐标，
        /// 取消/换目标传当前坐标），<see cref="HaulTicket.SalvageInstanceId"/> 保持不变，预留自然
        /// 释放（票据本身不再被任何地方引用）。</summary>
        public static GroundItemRecord DropHaul(CampaignState state, HaulTicket ticket, Vector2 dropPosition)
        {
            if (ticket == null)
            {
                return null;
            }
            return SpawnGroundItem(state, HomeValleyLayout.RegionId, dropPosition,
                ticket.ResourceType, ticket.Amount, ticket.SalvageInstanceId);
        }

        // ── 出发校验：货位需求计算（DEMO-CONTENT-LOCK.md §2.3）────────────────────

        /// <summary>单件货物占用的货位数。废料按 <see cref="HomeValleyLayout.ScrapUnitsPerCargoSlot"/>
        /// 向上取整（不足一箱也占 1 位，40 的整数倍每 40 占 1 位）；其余资源类型（完整模块/终端数据盒/
        /// 核心数据）按"件"计，不管数量，每种占 1 位——本 Story 只有废料是真实可产出的资源类型，其余
        /// 类型的换算规则先落数据，供 ER4-CONTENT-01 之后的真实模块/数据盒复用。</summary>
        public static int SlotsForCargo(string resourceType, int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }
            if (resourceType == CampaignEconomyLedger.ResourceScrap)
            {
                return (int)Math.Ceiling(amount / (double)HomeValleyLayout.ScrapUnitsPerCargoSlot);
            }
            return 1;
        }

        public readonly struct LoadoutCheckResult
        {
            public readonly bool Success;
            public readonly string FailureReason;
            public readonly int RequiredSlots;
            public readonly int AvailableSlots;

            private LoadoutCheckResult(bool success, string failureReason, int requiredSlots, int availableSlots)
            {
                Success = success;
                FailureReason = failureReason;
                RequiredSlots = requiredSlots;
                AvailableSlots = availableSlots;
            }

            public static LoadoutCheckResult Ok(int requiredSlots, int availableSlots) =>
                new LoadoutCheckResult(true, null, requiredSlots, availableSlots);

            public static LoadoutCheckResult Fail(string reason, int requiredSlots, int availableSlots) =>
                new LoadoutCheckResult(false, reason, requiredSlots, availableSlots);
        }

        /// <summary>出发校验：给定当前实际可用机器的货位总和与待携带货物清单，判断能否实际装车
        /// （DEMO-CONTENT-LOCK.md §2.3"出发校验的'有货舱'要求"）。<paramref name="availableMachineCargoSlots"/>
        /// 必须是调用方按当前真实机器名册（活着的、非直控占用、可出征）现算的结果——机器阵亡后货位
        /// 减少，本方法不做任何"默认三机"假设，也不读取任何全局状态，是纯函数（STORY-EXECUTION-
        /// CARDS.md #ER3-STO-01 第3条"机器阵亡后出发校验根据现有容量与主线最低需求逐项提示，不靠
        /// 默认假定"）。<paramref name="cargoItems"/> 用既有的 <see cref="CargoEntry"/>（复用
        /// <see cref="MachineRecord.Cargo"/>/<see cref="BuildingRecord.Inventory"/> 同一类型，不新造
        /// 一个平行的"货物"表示）。</summary>
        public static LoadoutCheckResult CheckExpeditionLoadout(
            IEnumerable<int> availableMachineCargoSlots,
            IEnumerable<CargoEntry> cargoItems)
        {
            int totalSlots = availableMachineCargoSlots?.Sum() ?? 0;
            int requiredSlots = (cargoItems ?? Array.Empty<CargoEntry>())
                .Sum(item => SlotsForCargo(item.ResourceType, item.Amount));

            return requiredSlots > totalSlots
                ? LoadoutCheckResult.Fail($"insufficient-cargo-slots:need={requiredSlots}:have={totalSlots}", requiredSlots, totalSlots)
                : LoadoutCheckResult.Ok(requiredSlots, totalSlots);
        }

        private static BuildingRecord FindBuilding(CampaignState state, string buildingTypeId)
        {
            return state?.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == buildingTypeId);
        }
    }
}
