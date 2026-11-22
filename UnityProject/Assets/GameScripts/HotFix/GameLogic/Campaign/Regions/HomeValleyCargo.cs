using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using GameLogic.Localization;

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
            /// <summary>ER5-INT-01：原地面物所属区域——storage-full/DropHaul 把物品放回地面时必须落回
            /// 这里而不是硬编码归还谷地。补在本 Story 是因为它是第一个真正从破碎都市调用
            /// <see cref="CommitHaul"/> 的调用方（此前 <see cref="TryReserveHaul"/>/<see cref="CommitHaul"/>
            /// 只服务归还谷地自己的残骸拆解掉落，两者恰好同一区域，这个字段缺失从未被真实触发过）；
            /// 不补的话，破碎都市开箱后仓满重试会把地面物悄悄"传送"到归还谷地，在当前区域再也找不到。</summary>
            public string RegionId;
        }

        /// <summary>家园存量总容量。FG4-ECO-01 起按物品表（<see cref="Economy.HomeInventory.Capacity(CampaignState, Economy.ItemDef)"/>）：
        /// 废料 = 核心缓存 + 运转中的仓库（核心缓存只收废料，ERD-ECO-003）；其余固体 = 运转中的仓库；保管库物品 = eco.vault.capacity。
        /// 物品表里没有的资源类型沿用 Demo 口径（只算仓库那一份）。</summary>
        public static int GetStorageCapacity(CampaignState state, string resourceType)
        {
            if (Economy.ItemCatalog.TryGetByResource(resourceType, out Economy.ItemDef def) && Economy.HomeInventory.IsStorable(def))
            {
                return Economy.HomeInventory.Capacity(state, def);
            }
            bool warehouseOperational = FindBuilding(state, HomeValleyLayout.BuildingTypeWarehouse)?.ConstructionState
                == BuildingConstructionState.Operational;
            int warehousePart = warehouseOperational ? HomeValleyLayout.WarehouseCapacity : 0;
            return resourceType == CampaignEconomyLedger.ResourceScrap ? HomeValleyLayout.CoreCacheCapacity + warehousePart : warehousePart;
        }

        /// <summary>当前家园存量已占用数量（废料 = <see cref="CampaignState.Scrap"/>；其余按 <see cref="Economy.HomeInventory"/>）。</summary>
        public static int GetStorageUsed(CampaignState state, string resourceType)
        {
            if (resourceType == CampaignEconomyLedger.ResourceScrap)
            {
                return state.Scrap;
            }
            return Economy.ItemCatalog.TryGetByResource(resourceType, out Economy.ItemDef def) ? Economy.HomeInventory.Stock(state, def) : 0;
        }

        public static int GetAvailableSpace(CampaignState state, string resourceType)
        {
            return Math.Max(0, GetStorageCapacity(state, resourceType) - GetStorageUsed(state, resourceType));
        }

        // ── 地面物 ──────────────────────────────────────────────────────────────

        /// <summary>EMGR-GL-01（DEBT-FG7ENV03-06）：按 ID 查地面物，O(1)。结果与线性扫“第一个匹配”相同。
        /// 约定：改地面物必须整体替换 <see cref="CampaignState.GroundItems"/> 数组（不得原地改元素或改已有记录的 ID）——索引按数组引用判新旧。</summary>
        public static GroundItemRecord FindGroundItem(CampaignState state, string groundItemId)
        {
            GroundIndex idx = IndexOf(state);
            if (idx == null)
            {
                return null;
            }
            if (groundItemId == null)
            {
                return state.GroundItems.FirstOrDefault(g => g != null && g.GroundItemId == null); // 键为空字典存不了，走线性扫（极少见）
            }
            return idx.ById.TryGetValue(groundItemId, out GroundItemRecord g0) ? g0 : null;
        }

        /// <summary>地面物索引（按 ID / 按 SalvageId，各取数组里第一个匹配）。挂在 state 上随 state 回收（弱表），读档换出的新 state 自己重建。</summary>
        private sealed class GroundIndex
        {
            public GroundItemRecord[] Source;
            public readonly Dictionary<string, GroundItemRecord> ById = new Dictionary<string, GroundItemRecord>(StringComparer.Ordinal);
            public readonly Dictionary<string, GroundItemRecord> BySalvage = new Dictionary<string, GroundItemRecord>(StringComparer.Ordinal);
            /// <summary>数组里有重复键（只可能来自外部整体写入）：删除时不能就地同步，下次查找整表重建。</summary>
            public bool HasDup;
            /// <summary>FG11-E2E-01 修复轮 4（DEBT-EMGRGL01-01 大批归零超 2 倍的根因之一）：<see cref="HomeValleyConstruction.DropForHaulBatch"/> 的 ID 占用表——
            /// 每份地面物的 SalvageInstanceId 与它在 ':' / '/' 前的每个前缀，带计数。首次用时按数组建一次，之后随 Add / 就地删除增减；
            /// 原来每批都按全部地面物重建并为每个前缀分配子串（3 万份地面物时每批约 9 万个字符串），托管堆越大越慢。数组整体换掉时随 Rebuild 作废。</summary>
            private Dictionary<string, int> _taken;

            public Dictionary<string, int> Taken
            {
                get
                {
                    if (_taken == null)
                    {
                        _taken = new Dictionary<string, int>(Source.Length * 3, StringComparer.Ordinal);
                        foreach (GroundItemRecord g in Source)
                        {
                            CountTaken(_taken, g?.SalvageInstanceId, 1);
                        }
                    }
                    return _taken;
                }
            }

            /// <summary>与 HomeValleyConstruction.AddTakenPrefixes 同一口径：ID 本身与它在 ':' / '/' 前的每个前缀。</summary>
            private static void CountTaken(Dictionary<string, int> taken, string sid, int delta)
            {
                if (string.IsNullOrEmpty(sid))
                {
                    return;
                }
                Bump(taken, sid, delta);
                for (int j = 0; j < sid.Length; j++)
                {
                    if (sid[j] == ':' || sid[j] == '/')
                    {
                        Bump(taken, sid.Substring(0, j), delta);
                    }
                }
            }

            private static void Bump(Dictionary<string, int> taken, string key, int delta)
            {
                int n = (taken.TryGetValue(key, out int c) ? c : 0) + delta;
                if (n > 0)
                {
                    taken[key] = n;
                }
                else
                {
                    taken.Remove(key);
                }
            }

            public void Untake(GroundItemRecord g)
            {
                if (_taken != null && g != null)
                {
                    CountTaken(_taken, g.SalvageInstanceId, -1);
                }
            }

            public void Add(GroundItemRecord g)
            {
                if (g == null)
                {
                    return;
                }
                if (_taken != null)
                {
                    CountTaken(_taken, g.SalvageInstanceId, 1);
                }
                if (g.GroundItemId != null && !ById.TryAdd(g.GroundItemId, g))
                {
                    HasDup = true;
                }
                if (g.SalvageInstanceId != null && !BySalvage.TryAdd(g.SalvageInstanceId, g))
                {
                    HasDup = true;
                }
            }

            public void Rebuild(GroundItemRecord[] source)
            {
                ById.Clear();
                BySalvage.Clear();
                HasDup = false;
                _taken = null;
                Source = source;
                for (int i = 0; i < source.Length; i++)
                {
                    Add(source[i]);
                }
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CampaignState, GroundIndex> GroundIndexes =
            new System.Runtime.CompilerServices.ConditionalWeakTable<CampaignState, GroundIndex>();

        /// <summary>取（必要时重建）索引：数组引用变了 = 外部整体替换过（CargoService / GroundLootExpiry / DropPodService / HomeValleyConstruction / 读档规整），整表重建一次。</summary>
        private static GroundIndex IndexOf(CampaignState state)
        {
            if (state == null)
            {
                return null;
            }
            state.GroundItems ??= Array.Empty<GroundItemRecord>();
            GroundIndex idx = GroundIndexes.GetValue(state, _ => new GroundIndex());
            if (!ReferenceEquals(idx.Source, state.GroundItems))
            {
                idx.Rebuild(state.GroundItems);
            }
            return idx;
        }

        /// <summary>FG11-E2E-01 修复轮 4：现有地面物里是否已占用这个落地 ID（ID 本身或某份 SalvageInstanceId 在 ':' / '/' 前的前缀），
        /// 与原先 DropForHaulBatch 每批现建的占用集合同一结论；占用表随索引就地维护，平摊 O(1)。</summary>
        internal static bool IsDropIdTaken(CampaignState state, string id) => IndexOf(state)?.Taken.ContainsKey(id) == true;

        /// <summary>家园能不能存这种资源：物品表里的固体（仓库）与保管库物品（FG4-ECO-01，关闭 DEBT-FG3LOG02-01）；流体、数字资源、实体、表里没有的都不行。
        /// 与 <see cref="CommitHaul"/> 的拒绝条件同源，搬运下令与交付前都先问这里。</summary>
        public static bool CanStore(string resourceType) =>
            resourceType == CampaignEconomyLedger.ResourceScrap
            || (Economy.ItemCatalog.TryGetByResource(resourceType, out Economy.ItemDef def) && Economy.HomeInventory.IsStorable(def));

        public static GroundItemRecord FindGroundItemBySalvageId(CampaignState state, string salvageInstanceId)
        {
            GroundIndex idx = IndexOf(state);
            if (idx == null)
            {
                return null;
            }
            if (salvageInstanceId == null)
            {
                return state.GroundItems.FirstOrDefault(g => g != null && g.SalvageInstanceId == null);
            }
            return idx.BySalvage.TryGetValue(salvageInstanceId, out GroundItemRecord g0) ? g0 : null;
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
            GroundItemRecord[] old = state.GroundItems; // 上面的查找已保证非空且索引对齐本数组
            var next = new GroundItemRecord[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = item;
            state.GroundItems = next;
            if (GroundIndexes.TryGetValue(state, out GroundIndex idx) && ReferenceEquals(idx.Source, old))
            {
                idx.Source = next; // 就地同步：批量生成时不必每件都整表重建
                idx.Add(item);
            }
            return item;
        }

        /// <summary>整批追加地面物（调用方已判重，见 <see cref="HomeValleyConstruction.DropForHaulBatch"/>）：一次换数组，索引就地同步，不必下次查找时整表重建。</summary>
        public static void AppendGroundItems(CampaignState state, IReadOnlyList<GroundItemRecord> items)
        {
            GroundItemRecord[] old = state.GroundItems ?? Array.Empty<GroundItemRecord>();
            var next = new GroundItemRecord[old.Length + items.Count];
            Array.Copy(old, next, old.Length);
            for (int i = 0; i < items.Count; i++)
            {
                next[old.Length + i] = items[i];
            }
            state.GroundItems = next;
            if (GroundIndexes.TryGetValue(state, out GroundIndex idx) && ReferenceEquals(idx.Source, old))
            {
                idx.Source = next;
                for (int i = 0; i < items.Count; i++)
                {
                    idx.Add(items[i]);
                }
            }
        }

        private static void RemoveGroundItem(CampaignState state, string groundItemId)
        {
            GroundItemRecord[] old = state.GroundItems ?? Array.Empty<GroundItemRecord>();
            GroundItemRecord[] next = old.Where(g => g.GroundItemId != groundItemId).ToArray();
            state.GroundItems = next;
            // 就地同步（无重复键时被删的就是索引里那一份）；有重复键或空 ID 时不同步，下次查找按新数组整表重建。
            if (GroundIndexes.TryGetValue(state, out GroundIndex idx) && ReferenceEquals(idx.Source, old) && !idx.HasDup && groundItemId != null)
            {
                idx.Source = next;
                if (idx.ById.TryGetValue(groundItemId, out GroundItemRecord gone))
                {
                    idx.ById.Remove(groundItemId);
                    idx.Untake(gone);
                    if (gone.SalvageInstanceId != null)
                    {
                        idx.BySalvage.Remove(gone.SalvageInstanceId);
                    }
                }
            }
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
                RegionId = item.RegionId,
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
            if (!CanStore(ticket.ResourceType))
            {
                // FG3-LOG-02：与仓满同样放回地面（票据预留时已从地面移除，直接返回失败会让物品凭空消失）。
                SpawnGroundItem(state, ticket.RegionId ?? HomeValleyLayout.RegionId, ticket.SourcePosition,
                    ticket.ResourceType, ticket.Amount, ticket.SalvageInstanceId);
                return StoreResult.Fail($"unsupported-resource-type:{ticket.ResourceType}");
            }

            // FG8-LOOT-01（FGR-LOOT-001 两种去向）：带身份的敌方加密物品按协议交付——加密固件变成固件库里的未破解芯片（不占仓库），
            // 未解析模块 / 数据核心进仓库并登记解析身份；交付不了（仓库放不下）时照常走下面的“仓满放回地面”，身份留着。
            if (Economy.FieldLootService.TryDeliverHaul(state, ticket.SalvageInstanceId, ticket.ResourceType, ticket.Amount))
            {
                return StoreResult.Ok();
            }

            int available = GetAvailableSpace(state, ticket.ResourceType);
            if (available < ticket.Amount)
            {
                SpawnGroundItem(state, ticket.RegionId ?? HomeValleyLayout.RegionId, ticket.SourcePosition,
                    ticket.ResourceType, ticket.Amount, ticket.SalvageInstanceId);
                // ER8-CONTENT-01 AC-AUD-001 仓满：货物已原样放回地面，提示玩家腾仓后可再搬。
                Feedback.FeedbackCues.RaiseAt(Feedback.FeedbackCueId.StorageFull, ticket.SourcePosition,
                    GameText.Format("home.cargo.0_1_put_back", CampaignEconomyLedger.ResourceDisplayName(ticket.ResourceType), ticket.Amount));
                return StoreResult.Fail($"storage-full:need={ticket.Amount}:have={available}");
            }

            if (!string.IsNullOrEmpty(ledgerTransactionId))
            {
                CampaignEconomyLedger.Commit(state, ledgerTransactionId);
            }
            else if (ticket.ResourceType == CampaignEconomyLedger.ResourceScrap)
            {
                state.Scrap += ticket.Amount;
                Economy.HomeInventory.Touch();
            }
            else
            {
                // FG4-ECO-01：其余物品经家园物资唯一写入口（容量上面刚查过，照数存入）。
                Economy.ItemCatalog.TryGetByResource(ticket.ResourceType, out Economy.ItemDef def);
                Economy.HomeInventory.Add(state, def, ticket.Amount, clampToSpace: false);
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
            return SpawnGroundItem(state, ticket.RegionId ?? HomeValleyLayout.RegionId, dropPosition,
                ticket.ResourceType, ticket.Amount, ticket.SalvageInstanceId);
        }

        // ── 出发校验：货位需求计算（DEMO-CONTENT-LOCK.md §2.3）────────────────────

        /// <summary>一份货物占用的货舱空间（FG8-LOOT-03 起与正式版货舱同一规则：<see cref="Economy.LootPickCatalog.SpaceOf"/>）。</summary>
        public static int SlotsForCargo(string resourceType, int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }
            // FG8-LOOT-03（FGR-LOOT-030）：Demo 的“废料每 40 占 1 位、其余每种 1 位”换成正式版的唯一空间规则（材料每 10 件 1 格、固件 1、组件 3……），
            // 与货舱界面、自动拾取、转移、三选一同一组函数（Economy.LootPickCatalog.SpaceOf），不另起一套格子。
            string itemId = Economy.ItemCatalog.TryGetByResource(resourceType, out Economy.ItemDef def) ? def.Id : resourceType;
            return Economy.LootPickCatalog.SpaceOf(itemId, amount);
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
