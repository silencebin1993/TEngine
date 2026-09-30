using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG3-LOG-02（FG03 FGR-LOG-006 虚影施工、FGR-LOG-007 全额返还；FG04 FGR-ECO-040 劳动岗接活）：施工现场（建筑虚影、传送带规划）的材料与进度规则。
    ///
    /// 状态机本身仍是 <see cref="HomeValleyWorkOrders"/> 的施工单（Build）：本类只回答“这个现场还要多少材料、材料到了多少、能施工到哪、
    /// 取消 / 被摧毁时材料去哪”，并提供施工队列、优先级、“优先建造这一片”、劳动力提示与传送带规划。
    ///
    /// 材料的去向（守恒：任何时刻一份材料只在一处——仓库、机器货舱、现场、地面物、已建成的建筑）：
    /// - 放置虚影不扣材料（库存不够也能放，FGR-LOG-003）；施工单进入待分配池，第一腿是“去仓库取料”。
    /// - 机器到了仓库（归还核心或运转中的仓库，离现场最近的那个）才从库存里取——一趟最多 build.carry_per_trip，库存有多少取多少；
    ///   取料经资源账本（消费型事务，一趟一笔）。所以材料在被取走之前可以被别的任务用掉（维修、生产），机器到了仓库发现没货就把
    ///   施工单转为“等待材料”，不重复扣料（FG03 负向“施工途中材料被别的任务用掉”）。
    /// - 运到现场后材料进虚影（<see cref="BuildingRecord.ConstructionDelivered"/> / <see cref="PlannedBeltRecord.Delivered"/>），施工进度不能超过已到的材料；
    ///   材料不够就回仓库再取一趟（同一台机器），库存为 0 则等待材料并放掉机器，有货后回到待分配池。
    /// - 取消虚影：已到现场的材料和机器货舱里的材料全额退回（<see cref="ReturnMaterials"/>：仓库有空间就入库，放不下的变成地面物，
    ///   并生成搬运单等仓库有空间时由机器搬走）。
    /// - 施工中被摧毁（<see cref="OnSiteDestroyed"/>）：已消耗的材料按进度比例掉落为地面物，没消耗的留在现场，虚影保留，进度归零。
    ///
    /// 不做玩家没要求的事（FGR-BASE-020）：机器只做玩家放下的虚影；优先级只由玩家改（队列里的↑↓、“优先建造这一片”），系统从不自动调整；
    /// 没货就等，不会自己去拆别的东西凑材料。全部状态在存档里（虚影的所需 / 已到材料、施工单的腿与进度、传送带规划），
    /// 由 <see cref="HomeValleyController.SimStep"/> 推进，与镜头在不在家园无关（FGR-BASE-021）。
    /// </summary>
    public static class HomeValleyConstruction
    {
        /// <summary>传送带规划施工单的目标 ID 前缀（"beltplan:&lt;planId&gt;"）。</summary>
        public const string BeltPlanPrefix = "beltplan:";

        /// <summary>格网传送带层里“规划中”的标记位（值 = 标记位 | 等级 + 1）。已建成的传送带层值是 1～3，不会与它相交。</summary>
        public const ushort PlannedBeltFlag = 0x80;

        /// <summary>“等待材料”的原因码：materials:&lt;还差&gt;:&lt;库存&gt;。</summary>
        public const string MaterialsReasonPrefix = "materials:";

        /// <summary>返还物搬运单等仓库腾出空间时的原因码（机器没有被占用）。</summary>
        public const string ReturnWaitReason = "return-waiting-space";

        public static int CarryPerTrip => Math.Max(1, GridContent.TuningInt("build.carry_per_trip"));
        public static float BeltSecondsPerCell => Math.Max(0.05f, GridContent.Tuning("build.belt_seconds_per_cell"));
        public static int PriorityMin => -1;
        public static int PriorityMax => 2;

        /// <summary>看得见的施工状态（规划增删、格子建成、优先级）变化时 +1：传送带规划的虚影网格与施工队列按它刷新。</summary>
        public static int Revision { get; private set; }

        /// <summary>家园里能接施工单的机器数（劳动岗，<see cref="TickLabor"/> 每游戏秒数一次）。</summary>
        public static int LaborCount { get; private set; } = -1;

        /// <summary>有施工单却没有任何机器能接（远征 / 全部禁用施工 / 覆盖外）：队列、悬停与通知都写明。</summary>
        public static bool NoLabor { get; private set; }

        private static float _laborTimer;
        private static readonly List<string> NoticeScratch = new List<string>(4);

        public static void Touch() => Revision++;

        /// <summary>FG0-ARCH-01：接到一个战役时清空瞬态（劳动力计数与计时，读档后第一秒重新数）。</summary>
        public static void ResetSessionState()
        {
            LaborCount = -1;
            NoLabor = false;
            _laborTimer = 0f;
            Revision++;
        }

        // ── 现场 ─────────────────────────────────────────────────────────────────

        /// <summary>施工材料 / 返还物的玩家名（文本键）：废料；传送带上的物品在物品表（FG4-ECO-01）之前按编号显示。</summary>
        public static string MaterialName(string resourceType)
        {
            if (resourceType == CampaignEconomyLedger.ResourceScrap)
            {
                return GameText.Get("build.material.scrap");
            }
            if (resourceType != null && resourceType.StartsWith("item:", StringComparison.Ordinal))
            {
                return GameText.Format("build.material.item", resourceType.Substring(5));
            }
            return GameText.Get("build.material.other");
        }

        public static bool IsBeltPlan(string targetId) => targetId != null && targetId.StartsWith(BeltPlanPrefix, StringComparison.Ordinal);

        public static PlannedBeltRecord FindPlan(CampaignState state, string targetId)
        {
            PlannedBeltRecord[] plans = state?.Grid?.PlannedBelts;
            if (plans == null || targetId == null)
            {
                return null;
            }
            string id = IsBeltPlan(targetId) ? targetId.Substring(BeltPlanPrefix.Length) : targetId;
            foreach (PlannedBeltRecord p in plans)
            {
                if (p != null && p.PlanId == id)
                {
                    return p;
                }
            }
            return null;
        }

        private static BuildingRecord FindSiteBuilding(CampaignState state, string targetId)
        {
            if (state?.BuildingRecords == null || targetId == null)
            {
                return null;
            }
            foreach (BuildingRecord b in state.BuildingRecords)
            {
                if (b != null && b.BuildingId == targetId)
                {
                    return b;
                }
            }
            return null;
        }

        /// <summary>传送带规划还没建成（且没取消）的格数。</summary>
        public static int UnbuiltCells(PlannedBeltRecord p)
        {
            int n = 0;
            if (p?.CellState != null)
            {
                foreach (int s in p.CellState)
                {
                    if (s == 0)
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        public static int BuiltCells(PlannedBeltRecord p)
        {
            int n = 0;
            if (p?.CellState != null)
            {
                foreach (int s in p.CellState)
                {
                    if (s == 1)
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        /// <summary>这个现场还缺多少材料没运到（不含正在机器货舱里的那一趟）。</summary>
        public static int MaterialsStillNeeded(CampaignState state, WorkOrderRecord order)
        {
            if (order == null)
            {
                return 0;
            }
            if (IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord p = FindPlan(state, order.TargetId);
                return p == null ? 0 : Math.Max(0, UnbuiltCells(p) * p.ScrapPerCell - p.Delivered);
            }
            BuildingRecord b = FindSiteBuilding(state, order.TargetId);
            return b == null ? 0 : Math.Max(0, b.ConstructionRequired - b.ConstructionDelivered);
        }

        /// <summary>这个现场一共要多少材料（传送带：没建成的格子；建筑：整座）与已经到了多少（显示用）。</summary>
        public static void SiteMaterials(CampaignState state, WorkOrderRecord order, out int required, out int delivered)
        {
            required = 0;
            delivered = 0;
            if (order == null)
            {
                return;
            }
            if (IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord p = FindPlan(state, order.TargetId);
                if (p != null)
                {
                    required = UnbuiltCells(p) * p.ScrapPerCell;
                    delivered = Math.Min(required, p.Delivered);
                }
                return;
            }
            BuildingRecord b = FindSiteBuilding(state, order.TargetId);
            if (b != null)
            {
                required = b.ConstructionRequired;
                delivered = b.ConstructionDelivered;
            }
        }

        /// <summary>现场位置（机器走过去施工的点）：建筑 = 占地中心；传送带规划 = 下一格还没建成的格子。</summary>
        public static Vector2 SitePosition(CampaignState state, WorkOrderRecord order)
        {
            if (IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord p = FindPlan(state, order.TargetId);
                if (p != null)
                {
                    for (int i = 0; i < p.CellState.Length; i++)
                    {
                        if (p.CellState[i] == 0)
                        {
                            return new Vector2(p.Xs[i], p.Ys[i]);
                        }
                    }
                    if (p.Xs.Length > 0)
                    {
                        return new Vector2(p.Xs[p.Xs.Length - 1], p.Ys[p.Ys.Length - 1]);
                    }
                }
                return HomeValleyLayout.Core.Position;
            }
            BuildingRecord b = FindSiteBuilding(state, order.TargetId);
            return b?.Position ?? HomeValleyLayout.Core.Position;
        }

        /// <summary>取料点：归还核心与运转中的仓库里离 <paramref name="near"/> 最近的一个（库存是两者共用的，取料点只决定机器走多远）。</summary>
        public static Vector2 StoragePosition(CampaignState state, Vector2 near)
        {
            Vector2 best = HomeValleyLayout.Core.Position;
            float bestD = float.MaxValue;
            if (state?.BuildingRecords != null)
            {
                foreach (BuildingRecord b in state.BuildingRecords)
                {
                    if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                    {
                        continue;
                    }
                    bool storage = b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore
                                   || (b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse && b.ConstructionState == BuildingConstructionState.Operational);
                    if (!storage)
                    {
                        continue;
                    }
                    float d = (b.Position - near).sqrMagnitude;
                    if (d < bestD || (Mathf.Approximately(d, bestD) && string.CompareOrdinal(b.BuildingTypeId, HomeValleyLayout.BuildingTypeCore) == 0))
                    {
                        bestD = d;
                        best = b.Position;
                    }
                }
            }
            return best;
        }

        // ── 取料与卸料（由施工单的到达回调调用）──────────────────────────────────────

        /// <summary>
        /// 机器到了仓库：从库存取这一趟的材料（还差多少、一趟上限、库存有多少，取三者最小）进货舱。经资源账本扣库存（一趟一笔消费事务，不会重复扣）。
        /// 返回取到的数量；0 = 仓库里没有货（材料被别的任务用掉了 / 本来就不够），调用方把施工单转为等待材料。
        /// </summary>
        internal static int TryFetch(CampaignState state, WorkOrderRecord order, MachineRecord machine)
        {
            if (machine == null)
            {
                return 0;
            }
            int need = MaterialsStillNeeded(state, order);
            int stock = Mathf.FloorToInt(state.Scrap);
            int load = Math.Min(need, Math.Min(CarryPerTrip, stock));
            if (load <= 0)
            {
                return 0;
            }
            string tx = order.WorkOrderId + ":fetch:" + order.FetchCount.ToString(CultureInfo.InvariantCulture);
            CampaignEconomyLedger.ProposeConsume(state, tx, order.TargetId, CampaignEconomyLedger.ResourceScrap, load);
            if (!CampaignEconomyLedger.Reserve(state, tx).Success)
            {
                CampaignEconomyLedger.Cancel(state, tx);
                return 0;
            }
            CampaignEconomyLedger.Commit(state, tx);
            order.FetchCount++;
            machine.Cargo = new[] { new CargoEntry { ResourceType = CampaignEconomyLedger.ResourceScrap, Amount = load } };
            Revision++;
            return load;
        }

        /// <summary>机器到了现场：货舱里的施工材料放进虚影（建筑第一次收到材料时转为“施工中”）。</summary>
        internal static void Deposit(CampaignState state, WorkOrderRecord order, MachineRecord machine)
        {
            int amount = CargoScrap(machine);
            if (amount <= 0)
            {
                return;
            }
            machine.Cargo = Array.Empty<CargoEntry>();
            if (IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord p = FindPlan(state, order.TargetId);
                if (p == null)
                {
                    ReturnMaterials(state, machine.WorldPosition, CampaignEconomyLedger.ResourceScrap, amount, order.WorkOrderId + ":orphan:" + order.FetchCount);
                    return;
                }
                p.Delivered += amount;
            }
            else
            {
                BuildingRecord b = FindSiteBuilding(state, order.TargetId);
                if (b == null)
                {
                    ReturnMaterials(state, machine.WorldPosition, CampaignEconomyLedger.ResourceScrap, amount, order.WorkOrderId + ":orphan:" + order.FetchCount);
                    return;
                }
                b.ConstructionDelivered += amount;
                if (b.ConstructionState == BuildingConstructionState.Planned)
                {
                    b.ConstructionState = BuildingConstructionState.Building;
                }
            }
            Revision++;
        }

        /// <summary>机器货舱里装着的施工材料（废料）。</summary>
        public static int CargoScrap(MachineRecord machine)
        {
            if (machine?.Cargo == null)
            {
                return 0;
            }
            int n = 0;
            foreach (CargoEntry c in machine.Cargo)
            {
                if (c.ResourceType == CampaignEconomyLedger.ResourceScrap)
                {
                    n += c.Amount;
                }
            }
            return n;
        }

        /// <summary>
        /// 施工单放掉机器（路径受阻、无法到达、被玩家接入、规划被挪走、取消、机器阵亡）时，机器货舱里这一趟的材料不能跟着机器走：
        /// <paramref name="dropOnly"/> = true（机器阵亡）时就地变成地面物；否则退回仓库（放不下的变地面物）。
        /// </summary>
        internal static void ReleaseCargo(CampaignState state, WorkOrderRecord order, MachineRecord machine, bool dropOnly)
        {
            int amount = CargoScrap(machine);
            if (amount <= 0)
            {
                return;
            }
            machine.Cargo = Array.Empty<CargoEntry>();
            Vector2 at = MachineRegistry.TryGetLivePosition(machine.LogicId, out Vector2 live) ? live : machine.WorldPosition;
            string dropId = order.WorkOrderId + ":cargo:" + order.FetchCount.ToString(CultureInfo.InvariantCulture) + ":" + order.RetryCount.ToString(CultureInfo.InvariantCulture);
            if (dropOnly)
            {
                DropForHaul(state, at, CampaignEconomyLedger.ResourceScrap, amount, dropId);
            }
            else
            {
                ReturnMaterials(state, at, CampaignEconomyLedger.ResourceScrap, amount, dropId);
            }
            Revision++;
        }

        /// <summary>返还 / 掉落的地面物标识：<paramref name="baseId"/> 还没被地上的物品占用就原样使用（旧存档与自检按它找得到），
        /// 否则追加 “#2、#3…” 直到不重复。地面物按标识去重（<see cref="HomeValleyCargo.SpawnGroundItem"/>），标识撞上时第二份会被吞掉。
        /// O(地面物数 × 撞号次数)，只在返还发生时。</summary>
        public static string FreshDropId(CampaignState state, string baseId)
        {
            string id = baseId;
            for (int n = 2; Taken(state, id); n++)
            {
                id = baseId + "#" + n.ToString(CultureInfo.InvariantCulture);
            }
            return id;
        }

        private static bool Taken(CampaignState state, string id)
        {
            GroundItemRecord[] items = state?.GroundItems;
            if (items == null)
            {
                return false;
            }
            for (int i = 0; i < items.Length; i++)
            {
                string sid = items[i]?.SalvageInstanceId;
                if (sid != null && sid.StartsWith(id, StringComparison.Ordinal)
                    && (sid.Length == id.Length || sid[id.Length] == ':' || sid[id.Length] == '/'))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把 <paramref name="amount"/> 放到地上等机器搬（FGR-LOG-007“仓库满时变地面物，由机器之后搬运”）：废料按每趟搬运量
        /// （<see cref="CarryPerTrip"/>）拆成几份，每份生成一张返还搬运单——整份一次搬的话，超过仓库总容量的一大份永远等不到空位。
        /// 仓库存不了的物品整份落地、不生成搬运单（DEBT-FG3LOG02-01）。返回实际落地的数量。
        /// </summary>
        public static int DropForHaul(CampaignState state, Vector2 at, string resourceType, int amount, string dropId)
        {
            if (state == null || amount <= 0)
            {
                return 0;
            }
            string baseId = FreshDropId(state, dropId);
            bool haulable = HomeValleyCargo.CanStore(resourceType);
            int chunk = haulable ? CarryPerTrip : amount;
            int left = amount;
            int dropped = 0;
            for (int part = 0; left > 0; part++)
            {
                int n = Math.Min(chunk, left);
                string id = part == 0 ? baseId : baseId + "/" + part.ToString(CultureInfo.InvariantCulture);
                GroundItemRecord drop = HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, at, resourceType, n, id);
                if (drop != null)
                {
                    dropped += n;
                    if (haulable)
                    {
                        HomeValleyWorkOrders.TryCreateHaulPool(state, drop.GroundItemId);
                    }
                }
                left -= n;
            }
            return dropped;
        }

        /// <summary>“缺料”原因码（materials:缺:库存）→ 玩家文字（右键虚影被拒时显示，与施工状态同一写法）。</summary>
        public static string DescribeMaterialsReason(string reason)
        {
            int need = 0;
            int stock = 0;
            string[] parts = reason != null ? reason.Substring(MaterialsReasonPrefix.Length).Split(':') : Array.Empty<string>();
            if (parts.Length > 0)
            {
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out need);
            }
            if (parts.Length > 1)
            {
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out stock);
            }
            return GameText.Format("build.status.waiting_materials", MaterialName(CampaignEconomyLedger.ResourceScrap),
                need.ToString(CultureInfo.InvariantCulture), stock.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>最近一次返还里入库 / 留在地上的数量（自检与状态行读）。</summary>
        public static int LastReturnedStored { get; private set; }
        public static int LastReturnedGrounded { get; private set; }

        /// <summary>
        /// 全额返还（FGR-LOG-007）：先入库（仓库有多少空间放多少），放不下的在 <paramref name="at"/> 变成地面物，并生成搬运单——
        /// 仓库腾出空间后由空闲机器搬走（仓满时搬运单等待，不占机器）。废料以外的物品（传送带上的物品）目前家园仓库还存不了（FG4-ECO-01 物品表），
        /// 一律留在地上（不消失）。<paramref name="dropId"/> 是这一份返还的唯一标识（同一标识只生成一次地面物）。
        /// </summary>
        public static void ReturnMaterials(CampaignState state, Vector2 at, string resourceType, int amount, string dropId)
        {
            LastReturnedStored = 0;
            LastReturnedGrounded = 0;
            if (state == null || amount <= 0)
            {
                return;
            }
            // 审查修复（FGR-LOG-006 / 007）：返还标识必须在地面上唯一——地面物按标识去重，同一标识的第二份会被当成“已经生成过”吞掉
            // （战略暂停中连续两次逐格取消传送带虚影、仓库满时就会发生）。标识已被占用时追加序号，不依赖时钟。
            dropId = FreshDropId(state, dropId);
            int fit = HomeValleyCargo.CanStore(resourceType)
                ? Math.Min(amount, HomeValleyCargo.GetAvailableSpace(state, resourceType))
                : 0;
            if (fit > 0)
            {
                GroundItemRecord stored = HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, at, resourceType, fit, dropId + ":in");
                HomeValleyCargo.HaulTicket ticket = stored != null ? HomeValleyCargo.TryReserveHaul(state, stored.GroundItemId) : null;
                if (ticket != null && HomeValleyCargo.CommitHaul(state, ticket).Success)
                {
                    LastReturnedStored = fit;
                }
                else
                {
                    // 不应发生（容量刚查过）：CommitHaul 失败时已把这一份放回地面（同一标识），按地面物处理。
                    GroundItemRecord back = HomeValleyCargo.FindGroundItemBySalvageId(state, dropId + ":in");
                    if (back != null)
                    {
                        LastReturnedGrounded += back.Amount;
                        HomeValleyWorkOrders.TryCreateHaulPool(state, back.GroundItemId);
                    }
                }
            }
            int rest = amount - fit;
            if (rest > 0)
            {
                LastReturnedGrounded += DropForHaul(state, at, resourceType, rest, dropId);
            }
            if (LastReturnedGrounded <= 0)
            {
                return;
            }
            int grounded = LastReturnedGrounded;
            string what = MaterialName(resourceType);
            GameLogic.Notifications.NotificationCenter.Post("storage_full",
                GameText.Format("build.return.grounded", what, grounded.ToString(CultureInfo.InvariantCulture)), new Vector3(at.x, 0f, at.y));
        }

        // ── 施工进度（由 HomeValleyWorkOrders.TickTimedWork 调用）──────────────────────

        public enum ProgressResult : byte
        {
            Working,
            /// <summary>已到的材料用完了，还没建完：回仓库再取一趟（库存为 0 就等待材料）。</summary>
            NeedMaterials,
            Complete,
            /// <summary>现场已经不在了（规划被取消）。</summary>
            SiteGone,
            /// <summary>传送带：身边的格子建完了，下一格离得远——机器带着这张施工单走到下一格（已到现场的材料留在规划里）。</summary>
            MoveOn,
        }

        /// <summary>推进一个施工单 <paramref name="dt"/> 游戏秒：进度不超过已到材料允许的上限；传送带按路径顺序一格一格建成、进内核。</summary>
        internal static ProgressResult TickProgress(CampaignState state, WorkOrderRecord order, float dt)
        {
            if (IsBeltPlan(order.TargetId))
            {
                return TickBeltPlan(state, order, dt);
            }
            BuildingRecord b = FindSiteBuilding(state, order.TargetId);
            if (b == null)
            {
                return ProgressResult.SiteGone;
            }
            float duration = Mathf.Max(0.01f, order.Duration);
            float cap = b.ConstructionRequired <= 0 ? duration : duration * Mathf.Clamp01((float)b.ConstructionDelivered / b.ConstructionRequired);
            order.Progress = Mathf.Min(order.Progress + dt, cap);
            if (order.Progress >= duration - 1e-4f)
            {
                return ProgressResult.Complete;
            }
            if (order.Progress >= cap - 1e-4f && b.ConstructionDelivered < b.ConstructionRequired)
            {
                return ProgressResult.NeedMaterials;
            }
            return ProgressResult.Working;
        }

        private static ProgressResult TickBeltPlan(CampaignState state, WorkOrderRecord order, float dt)
        {
            PlannedBeltRecord p = FindPlan(state, order.TargetId);
            if (p == null)
            {
                return ProgressResult.SiteGone;
            }
            // FG3-LOG-04：地下传送带两端一起建成——一次施工单位 = 两端的材料与两格的工时；其余每格一个单位。
            int unitCells = IsUnderground(p) ? 2 : 1;
            int unitCost = p.ScrapPerCell * unitCells;
            float sec = BeltSecondsPerCell * unitCells;
            order.Duration = sec;
            int next = NextUnbuilt(p);
            if (next < 0)
            {
                return ProgressResult.Complete;
            }
            if (p.Delivered < unitCost)
            {
                return ProgressResult.NeedMaterials;
            }
            order.Progress += dt;
            bool builtOne = false;
            while (next >= 0 && order.Progress >= sec - 1e-4f && p.Delivered >= unitCost)
            {
                // 审查修复：机器只在身边施工——下一格离它超过交互距离，就先走过去（已到现场的材料跟着这张施工单，不重新取料）。
                // 每次到达至少建一格，保证即使位置查不到（服务层自检的瞬时到达）也一定有进展。
                if (builtOne && !WithinBuildReach(order, p, next))
                {
                    return ProgressResult.MoveOn;
                }
                order.Progress = Mathf.Max(0f, order.Progress - sec);
                if (!CommitBeltCell(state, p, next))
                {
                    // 这一格已经铺不下去（不应发生：规划格被标记占住）——这一格（地下传送带是两端）作废，它的材料留在现场给下一格用。
                    for (int k = 0; k < unitCells && next + k < p.CellState.Length; k++)
                    {
                        p.CellState[next + k] = 2;
                        ClearPlannedMarker(HomeGridService.MapFor(state), new GridCell(p.Xs[next + k], p.Ys[next + k]));
                    }
                }
                else
                {
                    p.Delivered -= unitCost;
                    builtOne = true;
                }
                next = NextUnbuilt(p);
            }
            if (next < 0)
            {
                return ProgressResult.Complete;
            }
            if (builtOne && p.Delivered >= unitCost && !WithinBuildReach(order, p, next))
            {
                return ProgressResult.MoveOn;
            }
            if (p.Delivered < unitCost)
            {
                order.Progress = Mathf.Min(order.Progress, sec);
                return ProgressResult.NeedMaterials;
            }
            return ProgressResult.Working;
        }

        /// <summary>传送带施工的“身边”：机器与这一格的距离不超过交互距离（<see cref="RegionInteractionSystem.InteractRange"/>）。
        /// 查不到机器位置时按“在身边”处理（不因为缺位置卡住施工）。</summary>
        private static bool WithinBuildReach(WorkOrderRecord order, PlannedBeltRecord p, int cell)
        {
            if (order.AssignedMachineLogicId <= 0 || !MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord m))
            {
                return true;
            }
            Vector2 at = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
            Vector2 site = new Vector2(p.Xs[cell], p.Ys[cell]);
            return (at - site).sqrMagnitude <= RegionInteractionSystem.InteractRange * RegionInteractionSystem.InteractRange;
        }

        private static int NextUnbuilt(PlannedBeltRecord p)
        {
            for (int i = 0; i < p.CellState.Length; i++)
            {
                if (p.CellState[i] == 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool CommitBeltCell(CampaignState state, PlannedBeltRecord p, int i)
        {
            if (p.PipePiece > 0)
            {
                return CommitPipeCell(state, p, i);
            }
            var cell = new GridCell(p.Xs[i], p.Ys[i]);
            HomeGridMap map = HomeGridService.MapFor(state);
            ushort marker = map.GetBelt(cell);
            map.SetBelt(cell, 0);
            BeltOpResult r;
            switch ((BeltNodeKind)p.NodeKind)
            {
                case BeltNodeKind.Splitter:
                case BeltNodeKind.Merger:
                    // FG3-LOG-04：分流器 / 合流器建成后按规划里的设置（新放 = 默认；被摧毁的虚影 = 原设置）。
                    r = BeltNetworkService.TryPlaceNode(state, cell, (BeltDir)p.Dirs[i], p.Tier, (BeltNodeKind)p.NodeKind);
                    if (r.Ok)
                    {
                        ApplyNodeSettings(state, p, cell);
                    }
                    break;
                case BeltNodeKind.UndergroundIn:
                {
                    // FG3-LOG-04：地下传送带两端一起建成（Xs / Ys = [入口, 出口]）；出口格的规划标记也先放开。
                    if (p.Xs.Length < 2 || i != 0)
                    {
                        r = BeltOpResult.Kernel(BeltResult.InvalidArgument);
                        break;
                    }
                    var exit = new GridCell(p.Xs[1], p.Ys[1]);
                    ushort exitMarker = map.GetBelt(exit);
                    map.SetBelt(exit, 0);
                    int distance = Math.Abs(p.Xs[1] - p.Xs[0]) + Math.Abs(p.Ys[1] - p.Ys[0]);
                    r = BeltNetworkService.TryPlaceUnderground(state, cell, (BeltDir)p.Dirs[0], p.Tier, distance);
                    if (!r.Ok)
                    {
                        map.SetBelt(exit, exitMarker);
                    }
                    else
                    {
                        p.CellState[1] = 1;
                    }
                    break;
                }
                default:
                    r = BeltNetworkService.TryPlace(state, cell, (BeltDir)p.Dirs[i], p.Tier);
                    break;
            }
            if (!r.Ok)
            {
                map.SetBelt(cell, marker);
                return false;
            }
            p.CellState[i] = 1;
            Revision++;
            return true;
        }

        /// <summary>
        /// FG3-LOG-05：管线层的规划一格建成——放开规划标记后走正式入口 <see cref="PipeNetworkService.TryPlace"/>（格网规则 + 流体规则）。
        /// 规划到建成之间别处的网络可能已经换了流体（例如这段管线两头各接上了水与原油）：这时这一格建不成，发“失败”通知写明是哪两种流体，
        /// 这一格作废（它的材料留在现场给下一格，最后多出来的全额退回，见 <see cref="OnSiteCompleted"/>）。
        /// </summary>
        private static bool CommitPipeCell(CampaignState state, PlannedBeltRecord p, int i)
        {
            var cell = new GridCell(p.Xs[i], p.Ys[i]);
            HomeGridMap map = HomeGridService.MapFor(state);
            ushort marker = map.GetPipe(cell);
            map.SetPipe(cell, 0);
            var kind = (PipePieceKind)Math.Max(0, Math.Min(3, p.PipePiece - 1));
            PipeOpResult r = PipeNetworkService.TryPlace(state, cell, kind, p.Tier, p.Dirs[i]);
            if (!r.Ok)
            {
                map.SetPipe(cell, marker);
                if (r.Code == PipeResult.FluidConflict)
                {
                    GameLogic.Notifications.NotificationCenter.Post("failure",
                        GameText.Format("logistics.pipe.commit_conflict", cell.X, cell.Y, r.Args.Length > 0 ? r.Args[0] : string.Empty, r.Args.Length > 1 ? r.Args[1] : string.Empty),
                        new Vector3(cell.X, 0f, cell.Y));
                }
                else if (r.Code == PipeResult.ValveChained)
                {
                    GameLogic.Notifications.NotificationCenter.Post("failure",
                        GameText.Format("logistics.pipe.commit_failed", cell.X, cell.Y, r.Describe()), new Vector3(cell.X, 0f, cell.Y));
                }
                LastPipeCommitFailure = r.Describe();
                return false;
            }
            p.CellState[i] = 1;
            Revision++;
            return true;
        }

        /// <summary>FG3-LOG-05：最近一次管线虚影建不成的原因（自检读）。</summary>
        public static string LastPipeCommitFailure { get; private set; }

        /// <summary>这份规划占的是管线层（FG3-LOG-05）还是传送带层。</summary>
        public static bool IsPipePlan(PlannedBeltRecord p) => p != null && p.PipePiece > 0;

        private static ushort PlannedMarker(PlannedBeltRecord p) => (ushort)(PlannedBeltFlag | (p.Tier + 1));

        private static void SetPlannedMarker(HomeGridMap map, PlannedBeltRecord p, GridCell cell)
        {
            if (IsPipePlan(p))
            {
                map.SetPipe(cell, PlannedMarker(p));
            }
            else
            {
                map.SetBelt(cell, PlannedMarker(p));
            }
        }

        /// <summary>FG3-LOG-04：这份规划是不是地下传送带（两端一起建、一起取消）。</summary>
        public static bool IsUnderground(PlannedBeltRecord p) => p != null && p.NodeKind == (int)BeltNodeKind.UndergroundIn;

        /// <summary>FG3-LOG-04：把规划里记的节点设置写进刚建成的分流器 / 合流器（0 = 默认）。</summary>
        private static void ApplyNodeSettings(CampaignState state, PlannedBeltRecord p, GridCell cell)
        {
            if (p.NodeKind == (int)BeltNodeKind.Splitter)
            {
                int rl = Math.Max(1, Math.Min(BeltConst.RatioMax, p.RatioL));
                int rr = Math.Max(1, Math.Min(BeltConst.RatioMax, p.RatioR));
                BeltNetworkService.TrySetSplitter(state, cell, rl, rr, (BeltSide)Math.Max(0, Math.Min(2, p.PriorityOut)),
                    (ushort)Math.Max(0, Math.Min(ushort.MaxValue, p.FilterL)), (ushort)Math.Max(0, Math.Min(ushort.MaxValue, p.FilterR)));
            }
            else if (p.NodeKind == (int)BeltNodeKind.Merger && p.PriorityIn != 0)
            {
                BeltNetworkService.TrySetMergerPriority(state, cell, (BeltSide)Math.Max(0, Math.Min(2, p.PriorityIn)));
            }
        }

        /// <summary>现场建完：建筑记下投入并清掉施工字段；传送带规划从存档移除。</summary>
        internal static void OnSiteCompleted(CampaignState state, WorkOrderRecord order, BuildingRecord building)
        {
            if (IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord p = FindPlan(state, order.TargetId);
                if (p != null)
                {
                    if (p.Delivered > 0)
                    {
                        // 有格子作废（不应发生）时多出来的材料退回，不丢。
                        ReturnMaterials(state, SitePosition(state, order), CampaignEconomyLedger.ResourceScrap, p.Delivered, order.WorkOrderId + ":leftover");
                        p.Delivered = 0;
                    }
                    RemovePlan(state, p);
                }
                return;
            }
            if (building != null)
            {
                building.InvestedScrap = Math.Max(building.InvestedScrap, building.ConstructionRequired);
                building.ConstructionRequired = 0;
                building.ConstructionDelivered = 0;
            }
            Revision++;
        }

        // ── 取消与摧毁 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 取消施工（玩家在拆除模式里取消规划 / 施工单被取消）：已运到现场的材料全额退回（FGR-LOG-006“已经预留的材料全额退回”），
        /// 传送带规划里还没建成的格子一并取消（已经建成的格子是真正的传送带，留着）。机器货舱里的那一趟由调用方先退。
        /// </summary>
        internal static void OnSiteCancelled(CampaignState state, WorkOrderRecord order)
        {
            if (IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord p = FindPlan(state, order.TargetId);
                if (p == null)
                {
                    return;
                }
                HomeGridMap map = HomeGridService.MapFor(state);
                Vector2 at = SitePosition(state, order);
                for (int i = 0; i < p.CellState.Length; i++)
                {
                    if (p.CellState[i] == 0)
                    {
                        p.CellState[i] = 2;
                        ClearPlannedMarker(map, new GridCell(p.Xs[i], p.Ys[i]));
                    }
                }
                if (p.Delivered > 0)
                {
                    ReturnMaterials(state, at, CampaignEconomyLedger.ResourceScrap, p.Delivered, order.WorkOrderId + ":refund");
                    p.Delivered = 0;
                }
                RemovePlan(state, p);
                return;
            }
            BuildingRecord b = FindSiteBuilding(state, order.TargetId);
            if (b != null && b.ConstructionDelivered > 0)
            {
                ReturnMaterials(state, b.Position, CampaignEconomyLedger.ResourceScrap, b.ConstructionDelivered, order.WorkOrderId + ":refund");
                b.ConstructionDelivered = 0;
            }
            Revision++;
        }

        /// <summary>最近一次施工现场被摧毁时掉落 / 留在现场的材料（自检读）。</summary>
        public static int LastDestroyedDropped { get; private set; }
        public static int LastDestroyedKept { get; private set; }

        /// <summary>
        /// FG03 负向“施工中的建筑被突袭摧毁”：已消耗的材料按进度比例掉落为地面物（机器之后搬回仓库），没消耗的留在现场，虚影保留、进度归零，
        /// 施工单回到待分配池（正在干活的机器放下这单，货舱里的材料退回）。返回 false = 不是施工中的虚影。
        /// 家园突袭的伤害结算（FG6-DEF-05）打到虚影时调用本入口；目前家园还没有会打虚影的敌人，由自检直接驱动（见 ADR-LOG-002）。
        /// </summary>
        public static bool OnSiteDestroyed(CampaignState state, string buildingId)
        {
            LastDestroyedDropped = 0;
            LastDestroyedKept = 0;
            BuildingRecord b = FindSiteBuilding(state, buildingId);
            if (b == null || !HomeValleyController.IsPlannedGhost(b) || HomeGridService.IsRelocationGhost(b))
            {
                return false;
            }
            WorkOrderRecord order = HomeValleyWorkOrders.FindActiveBuild(state, buildingId);
            float fraction = order != null && order.Duration > 0f ? Mathf.Clamp01(order.Progress / order.Duration) : 0f;
            int consumed = Math.Min(b.ConstructionDelivered, Mathf.FloorToInt(b.ConstructionRequired * fraction));
            if (consumed > 0)
            {
                string dropId = buildingId + ":wreck:" + (order?.WorkOrderId ?? "none") + ":" + (order?.FetchCount ?? 0).ToString(CultureInfo.InvariantCulture);
                DropForHaul(state, b.Position, CampaignEconomyLedger.ResourceScrap, consumed, dropId);
            }
            b.ConstructionDelivered -= consumed;
            b.ConstructionState = b.ConstructionDelivered > 0 ? BuildingConstructionState.Building : BuildingConstructionState.Planned;
            LastDestroyedDropped = consumed;
            LastDestroyedKept = b.ConstructionDelivered;
            if (order != null)
            {
                HomeValleyWorkOrders.ResetForRebuild(state, order);
            }
            GameLogic.Notifications.NotificationCenter.Post("failure",
                GameText.Format("build.site.destroyed", HomeGridService.DisplayName(b.BuildingTypeId), consumed.ToString(CultureInfo.InvariantCulture)),
                new Vector3(b.Position.x, 0f, b.Position.y));
            Revision++;
            return true;
        }

        // ── 传送带规划（DEBT-FG3LOG01-01）──────────────────────────────────────────

        /// <summary>
        /// 按已校验过的拖拽规划放下传送带虚影：记一份规划（格、朝向、等级、单价），占住格网传送带层（标记位），生成一张施工单（先去仓库取料）。
        /// 不扣材料；材料不够也能放（虚影等材料）。调用方已经保证逐格合法、没超长、已解锁。
        /// </summary>
        public static string PlanBelts(CampaignState state, BeltPathPlan plan)
        {
            GridState grid = state.Grid;
            string planId = "b" + grid.NextBeltPlanSerial.ToString(CultureInfo.InvariantCulture);
            grid.NextBeltPlanSerial++;
            int n = plan.Cells.Count;
            var rec = new PlannedBeltRecord
            {
                PlanId = planId,
                Tier = plan.Tier,
                ScrapPerCell = Math.Max(0, plan.ScrapPerCell),
                Xs = new int[n],
                Ys = new int[n],
                Dirs = new int[n],
                CellState = new int[n],
                Delivered = 0,
                // FG3-LOG-04：放的是什么（传送带 / 分流器 / 合流器 / 地下传送带）；节点的设置取默认（1:1、不设优先口、全部物品）。
                NodeKind = plan.IsPipe ? 0 : (int)plan.Kind,
                RatioL = 1,
                RatioR = 1,
                // FG3-LOG-05：管线层的规划（管线 / 泵 / 储罐 / 阀门）占格网管线层。
                PipePiece = plan.IsPipe ? (int)plan.Pipe + 1 : 0,
                PipeFluid = plan.IsPipe ? plan.PipeFluid : 0,
            };
            HomeGridMap map = HomeGridService.MapFor(state);
            for (int i = 0; i < n; i++)
            {
                rec.Xs[i] = plan.Cells[i].X;
                rec.Ys[i] = plan.Cells[i].Y;
                rec.Dirs[i] = (int)plan.Dirs[i];
                SetPlannedMarker(map, rec, plan.Cells[i]);
            }
            PlannedBeltRecord[] old = grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>();
            var next = new PlannedBeltRecord[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = rec;
            grid.PlannedBelts = next;
            HomeValleyWorkOrders.CreateConstructionOrder(state, BeltPlanPrefix + planId, rec.ScrapPerCell * n, BeltSecondsPerCell * (IsUnderground(rec) ? 2 : 1));
            Revision++;
            return planId;
        }

        private static void RemovePlan(CampaignState state, PlannedBeltRecord p)
        {
            PlannedBeltRecord[] old = state.Grid?.PlannedBelts;
            if (old == null)
            {
                return;
            }
            var list = new List<PlannedBeltRecord>(old.Length);
            foreach (PlannedBeltRecord r in old)
            {
                if (r != null && !ReferenceEquals(r, p))
                {
                    list.Add(r);
                }
            }
            state.Grid.PlannedBelts = list.ToArray();
            Revision++;
        }

        /// <summary>这一格是不是规划中（还没建成）的传送带。</summary>
        public static bool TryFindPlannedCell(CampaignState state, GridCell cell, out PlannedBeltRecord plan, out int index)
        {
            plan = null;
            index = -1;
            PlannedBeltRecord[] plans = state?.Grid?.PlannedBelts;
            if (plans == null)
            {
                return false;
            }
            foreach (PlannedBeltRecord p in plans)
            {
                if (p?.Xs == null)
                {
                    continue;
                }
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (p.CellState[i] == 0 && p.Xs[i] == cell.X && p.Ys[i] == cell.Y)
                    {
                        plan = p;
                        index = i;
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool IsPlannedMarker(ushort beltLayerValue) => (beltLayerValue & PlannedBeltFlag) != 0;

        private static void ClearPlannedMarker(HomeGridMap map, GridCell cell)
        {
            if (IsPlannedMarker(map.GetBelt(cell)))
            {
                map.SetBelt(cell, 0);
            }
            if (IsPlannedMarker(map.GetPipe(cell)))
            {
                map.SetPipe(cell, 0); // FG3-LOG-05：管线层的虚影
            }
        }

        /// <summary>
        /// 取消规划中的传送带格（拆除模式点 / 框选到虚影格）。每份规划：这些格子作废、放开格网；运到现场的材料超过剩下格子需要的部分退回；
        /// 格子全取消了，施工单一并取消（已到材料全额退回）。返回取消的格数。
        /// </summary>
        public static int CancelPlannedCells(CampaignState state, List<GridCell> cells)
        {
            if (state == null || cells == null || cells.Count == 0)
            {
                return 0;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            int cancelled = 0;
            var touched = new List<PlannedBeltRecord>(2);
            foreach (GridCell c in cells)
            {
                if (!TryFindPlannedCell(state, c, out PlannedBeltRecord p, out int i))
                {
                    continue;
                }
                // FG3-LOG-04：地下传送带的规划两端一起取消（不会只剩半条）。
                int from = IsUnderground(p) ? 0 : i;
                int to = IsUnderground(p) ? p.CellState.Length - 1 : i;
                for (int k = from; k <= to; k++)
                {
                    if (p.CellState[k] != 0)
                    {
                        continue;
                    }
                    p.CellState[k] = 2;
                    ClearPlannedMarker(map, new GridCell(p.Xs[k], p.Ys[k]));
                    cancelled++;
                }
                if (!touched.Contains(p))
                {
                    touched.Add(p);
                }
            }
            foreach (PlannedBeltRecord p in touched)
            {
                string target = BeltPlanPrefix + p.PlanId;
                WorkOrderRecord order = HomeValleyWorkOrders.FindActiveBuild(state, target);
                if (UnbuiltCells(p) == 0)
                {
                    if (order != null)
                    {
                        HomeValleyWorkOrders.CancelOrder(state, order.WorkOrderId, SitePosition(state, order));
                    }
                    else
                    {
                        RemovePlan(state, p);
                    }
                    continue;
                }
                int need = UnbuiltCells(p) * p.ScrapPerCell;
                if (p.Delivered > need)
                {
                    int excess = p.Delivered - need;
                    p.Delivered = need;
                    ReturnMaterials(state, new Vector2(cells[0].X, cells[0].Y), CampaignEconomyLedger.ResourceScrap, excess, target + ":trim");
                }
            }
            Revision++;
            return cancelled;
        }

        /// <summary>换图后把规划中的传送带重新标进格网传送带层（与“传送带层由内核推导”同一纪律：真相在存档的规划里）。O(规划格数)，只在建图时。</summary>
        public static void ApplyPlannedBelts(CampaignState state, HomeGridMap map)
        {
            PlannedBeltRecord[] plans = state?.Grid?.PlannedBelts;
            if (plans == null || map == null)
            {
                return;
            }
            foreach (PlannedBeltRecord p in plans)
            {
                if (p?.Xs == null)
                {
                    continue;
                }
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (p.CellState[i] == 0)
                    {
                        SetPlannedMarker(map, p, new GridCell(p.Xs[i], p.Ys[i]));
                    }
                }
            }
        }

        /// <summary>全部规划中（未建成）的传送带格数（HUD / 自检）。</summary>
        public static int PlannedCellCount(CampaignState state)
        {
            int n = 0;
            foreach (PlannedBeltRecord p in state?.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                n += UnbuiltCells(p);
            }
            return n;
        }

        // ── 旧存档迁移 ───────────────────────────────────────────────────────────

        /// <summary>
        /// FG3-LOG-01 之前的存档里，放置时就把全部废料预留进了施工单的资源事务（Reserved / Running）。读到这样的施工单：
        /// 预留的废料算作“已运到现场”（事务确认消费，材料在虚影里），所需材料按建造表补齐，施工单按现场继续（第 0 腿）。
        /// 守恒：库存不变、材料不丢不重复。施工单上的事务 ID 清掉，之后不再走旧的“完工确认 / 取消退款”。每张施工单只迁移一次。
        /// </summary>
        public static void MigrateLegacyBuildOrder(CampaignState state, WorkOrderRecord order)
        {
            if (order == null || order.Kind != WorkOrderKind.Build || string.IsNullOrEmpty(order.ResourceTransactionId) || IsBeltPlan(order.TargetId))
            {
                return;
            }
            BuildingRecord b = FindSiteBuilding(state, order.TargetId);
            ResourceTransactionRecord tx = CampaignEconomyLedger.Find(state, order.ResourceTransactionId);
            if (b != null && string.IsNullOrEmpty(b.RelocateFromId) && tx != null
                && (tx.State == ResourceTransactionState.Reserved || tx.State == ResourceTransactionState.Running))
            {
                int reserved = Mathf.RoundToInt(tx.Reserved);
                CampaignEconomyLedger.Commit(state, order.ResourceTransactionId);
                int required = HomeValleyLayout.BuildProfile.TryGetValue(b.BuildingTypeId, out (int ScrapCost, float Seconds) p) ? p.ScrapCost : reserved;
                b.ConstructionRequired = Math.Max(required, reserved);
                b.ConstructionDelivered = reserved;
                order.Leg = b.ConstructionDelivered < b.ConstructionRequired ? 1 : 0;
            }
            order.ResourceTransactionId = null;
            MigratedLegacyOrders++;
        }

        /// <summary>本进程迁移过的旧施工单数（自检读）。</summary>
        public static int MigratedLegacyOrders { get; private set; }

        // ── 劳动力 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 每游戏秒数一次家园里能接施工单的机器（活着、在家园、不在厂内、没被接入、施工偏好 &gt; 0、在信号覆盖内、底盘能施工），O(机器数)。
        /// 有施工单但一台都没有 → <see cref="NoLabor"/>，并发一条可定位的警告（只在从有到没有的那一刻发，不刷屏）。
        /// </summary>
        public static void TickLabor(CampaignState state, float dt, Func<int, bool> isDirectControlled)
        {
            _laborTimer -= dt;
            if (_laborTimer > 0f && LaborCount >= 0)
            {
                return;
            }
            _laborTimer = 1f;
            int labor = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != HomeValleyLayout.RegionId || m.IsInFactory)
                {
                    continue;
                }
                if (isDirectControlled != null && isDirectControlled(m.LogicId))
                {
                    continue;
                }
                if (!HomeValleyWorkOrders.CanDoKind(m.ChassisId, WorkOrderKind.Build) || (m.WorkPriorities?.Get(WorkOrderKind.Build) ?? 0) <= 0)
                {
                    continue;
                }
                if (Signal.SignalCoverageService.IsMachineOutOfCoverage(m.LogicId))
                {
                    continue;
                }
                labor++;
            }
            LaborCount = labor;
            bool pending = HomeValleyWorkOrders.CountActiveConstruction(state) > 0;
            bool noLabor = pending && labor == 0;
            if (noLabor && !NoLabor)
            {
                Vector2 at = FirstConstructionPosition(state);
                GameLogic.Notifications.NotificationCenter.Post("construction_no_labor", GameText.Get("build.status.no_labor"), new Vector3(at.x, 0f, at.y));
                Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstNoLabor);
            }
            if (noLabor != NoLabor)
            {
                Revision++;
            }
            NoLabor = noLabor;
        }

        private static Vector2 FirstConstructionPosition(CampaignState state)
        {
            foreach (WorkOrderRecord o in state?.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.Kind == WorkOrderKind.Build && HomeValleyWorkOrders.IsActive(o))
                {
                    return SitePosition(state, o);
                }
            }
            return HomeValleyLayout.Core.Position;
        }

        /// <summary>施工单转为“等待材料”那一刻调用：发一条可定位的警告（同类聚合，B08）与首次引导钩子。</summary>
        internal static void NotifyWaitingMaterials(CampaignState state, WorkOrderRecord order, int need)
        {
            Vector2 at = SitePosition(state, order);
            GameLogic.Notifications.NotificationCenter.Post("construction_waiting",
                GameText.Format("build.notify.waiting", HomeValleyWorkOrders.DescribeTarget(state, order),
                    MaterialName(CampaignEconomyLedger.ResourceScrap), need.ToString(CultureInfo.InvariantCulture)),
                new Vector3(at.x, 0f, at.y));
            Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstWaitingMaterials);
            Revision++;
        }

        // ── 施工队列与优先级（FGR-LOG-006“施工队列可以查看、可以调整优先级”；FG03 第 4 节“优先建造这一片”）─────────────

        public readonly struct QueueEntry
        {
            /// <summary>施工单；被摧毁、等待确认重建的传送带虚影没有施工单（为 null，见 <see cref="DestroyedPlanId"/>）。</summary>
            public readonly WorkOrderRecord Order;
            public readonly string Name;
            public readonly string Status;
            public readonly float Fraction;
            public readonly Vector2 Position;
            /// <summary>FG3-LOG-03（FGR-LOG-027）：被摧毁、等待确认重建的传送带虚影的规划 ID（普通施工行为 null）。</summary>
            public readonly string DestroyedPlanId;

            public QueueEntry(WorkOrderRecord order, string name, string status, float fraction, Vector2 position, string destroyedPlanId = null)
            {
                Order = order;
                Name = name;
                Status = status;
                Fraction = fraction;
                Position = position;
                DestroyedPlanId = destroyedPlanId;
            }

            public bool IsDestroyedGhost => DestroyedPlanId != null;
        }

        /// <summary>
        /// 施工队列：全部未完成的施工单（新建、传送带规划、搬迁），优先级高的在前、同级先放的在前（机器领单时另按自己的工作偏好与距离，ERD-WRK-002）。O(工单数 log 工单数)，
        /// 只在队列面板打开时按 <see cref="Revision"/> 刷新。
        /// </summary>
        public static void CollectQueue(CampaignState state, List<QueueEntry> into)
        {
            into.Clear();
            if (state?.WorkOrders == null)
            {
                return;
            }
            var orders = new List<(WorkOrderRecord Order, int Index)>();
            for (int i = 0; i < state.WorkOrders.Length; i++)
            {
                WorkOrderRecord o = state.WorkOrders[i];
                if (o != null && o.Kind == WorkOrderKind.Build && HomeValleyWorkOrders.IsActive(o))
                {
                    orders.Add((o, i));
                }
            }
            // 优先级高的在前；同级按放下的先后（工单数组是追加顺序，同一游戏时刻放下的几座也分得出先后，不按随机的工单 ID）。
            orders.Sort((a, b) =>
            {
                if (a.Order.Priority != b.Order.Priority)
                {
                    return b.Order.Priority.CompareTo(a.Order.Priority);
                }
                if (a.Order.CreatedTick != b.Order.CreatedTick)
                {
                    return a.Order.CreatedTick.CompareTo(b.Order.CreatedTick);
                }
                return a.Index.CompareTo(b.Index);
            });
            foreach ((WorkOrderRecord o, int _) in orders)
            {
                into.Add(new QueueEntry(o, HomeValleyWorkOrders.DescribeTarget(state, o), DescribeStatus(state, o), Fraction(state, o), SitePosition(state, o)));
            }
            // FG3-LOG-03：被摧毁的传送带留下的虚影排在最后（没有施工单，等玩家点“重建”）。
            foreach (PlannedBeltRecord p in state.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                if (p != null && p.Destroyed && UnbuiltCells(p) > 0)
                {
                    int i = NextUnbuilt(p);
                    into.Add(new QueueEntry(null, DestroyedName(p, i), DestroyedStatus(p, i), 0f, new Vector2(p.Xs[i], p.Ys[i]), p.PlanId));
                }
            }
        }

        // ── 被摧毁的传送带（FGR-LOG-027）──────────────────────────────────────────────

        private static string DestroyedName(PlannedBeltRecord p, int i) =>
            GameText.Format("build.queue.destroyed_name", PieceName(p), p.Xs[i].ToString(CultureInfo.InvariantCulture), p.Ys[i].ToString(CultureInfo.InvariantCulture));

        /// <summary>FG3-LOG-04：规划里放的物流件的玩家名（传送带 T1 / 分流器 / 合流器 / 地下传送带 T1）。</summary>
        public static string PieceName(PlannedBeltRecord p) => p == null ? string.Empty
            : IsPipePlan(p) ? PipeNetworkService.PieceName((PipePieceKind)Math.Max(0, Math.Min(3, p.PipePiece - 1)), p.Tier)
            : BeltNetworkService.PieceName((BeltNodeKind)p.NodeKind, p.Tier);

        private static string DestroyedStatus(PlannedBeltRecord p, int i) =>
            GameText.Format("build.queue.destroyed_status", GameText.Get(GridMath.DirTextKey((GridDir)p.Dirs[i])));

        /// <summary>
        /// FG3-LOG-03（FGR-LOG-027“被摧毁时原位置留下虚影”）：在 <paramref name="cell"/> 放一份“被摧毁”的传送带虚影（一格，保留朝向与等级、每格造价按当前菜单），
        /// 占住格网（别的东西放不上去）。没有施工单：玩家在施工队列里点“重建”（或 FG6-DEF-03 的区域自动重建规则）才开工；拆除模式点它 = 移除虚影。
        /// 返回规划 ID。
        /// </summary>
        public static string AddDestroyedBeltGhost(CampaignState state, GridCell cell, BeltDir dir, int tier)
        {
            GridState grid = state.Grid;
            string planId = "b" + grid.NextBeltPlanSerial.ToString(CultureInfo.InvariantCulture);
            grid.NextBeltPlanSerial++;
            var rec = new PlannedBeltRecord
            {
                PlanId = planId,
                Tier = tier,
                ScrapPerCell = Math.Max(0, HomeGridService.BeltCostPerCell(tier)),
                Xs = new[] { cell.X },
                Ys = new[] { cell.Y },
                Dirs = new[] { (int)dir },
                CellState = new[] { 0 },
                Delivered = 0,
                Destroyed = true,
            };
            HomeGridService.MapFor(state).SetBelt(cell, (ushort)(PlannedBeltFlag | (tier + 1)));
            PlannedBeltRecord[] old = grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>();
            var next = new PlannedBeltRecord[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = rec;
            grid.PlannedBelts = next;
            Revision++;
            return planId;
        }

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-027 的节点版）：分流器 / 合流器 / 地下传送带被摧毁后留下保留原设置的虚影——分流器记比例、优先口、过滤，合流器记优先口，
        /// 地下传送带两端一份（[入口, 出口]，两端一起重建）。没有施工单：玩家在施工队列里点“重建”才开工（自动重建规则在 FG6-DEF-03）。返回规划 ID。
        /// </summary>
        public static string AddDestroyedNodeGhost(CampaignState state, BeltNodeInfo node)
        {
            GridState grid = state.Grid;
            string planId = "b" + grid.NextBeltPlanSerial.ToString(CultureInfo.InvariantCulture);
            grid.NextBeltPlanSerial++;
            bool under = node.Kind == BeltNodeKind.UndergroundIn || node.Kind == BeltNodeKind.UndergroundOut;
            BeltNodeKind kind = under ? BeltNodeKind.UndergroundIn : node.Kind;
            var rec = new PlannedBeltRecord
            {
                PlanId = planId,
                Tier = node.Tier,
                ScrapPerCell = Math.Max(0, HomeGridService.PieceCost(kind, node.Tier)),
                Xs = under ? new[] { node.EntranceX, node.ExitX } : new[] { node.X },
                Ys = under ? new[] { node.EntranceY, node.ExitY } : new[] { node.Y },
                Dirs = under ? new[] { (int)node.Dir, (int)node.Dir } : new[] { (int)node.Dir },
                CellState = under ? new[] { 0, 0 } : new[] { 0 },
                Delivered = 0,
                Destroyed = true,
                NodeKind = (int)kind,
                RatioL = Math.Max(1, node.RatioL),
                RatioR = Math.Max(1, node.RatioR),
                PriorityOut = (int)node.PriorityOut,
                FilterL = node.FilterL,
                FilterR = node.FilterR,
                PriorityIn = (int)node.PriorityIn,
            };
            HomeGridMap map = HomeGridService.MapFor(state);
            for (int i = 0; i < rec.Xs.Length; i++)
            {
                map.SetBelt(new GridCell(rec.Xs[i], rec.Ys[i]), (ushort)(PlannedBeltFlag | (node.Tier + 1)));
            }
            PlannedBeltRecord[] old = grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>();
            var next = new PlannedBeltRecord[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = rec;
            grid.PlannedBelts = next;
            Revision++;
            return planId;
        }

        /// <summary>被摧毁、等待确认重建的传送带虚影格数（施工队列“全部重建（N）”）。</summary>
        public static int DestroyedGhostCount(CampaignState state)
        {
            int n = 0;
            foreach (PlannedBeltRecord p in state?.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                if (p != null && p.Destroyed)
                {
                    n += UnbuiltCells(p);
                }
            }
            return n;
        }

        /// <summary>
        /// 玩家确认重建一份被摧毁的传送带虚影：按原设置（朝向、等级）生成施工单，之后与普通传送带虚影相同（机器取料、一格一格建成进内核）。
        /// 返回是否安排了。
        /// </summary>
        public static bool RebuildDestroyed(CampaignState state, string planId)
        {
            PlannedBeltRecord p = FindPlan(state, planId);
            if (p == null || !p.Destroyed || UnbuiltCells(p) == 0)
            {
                return false;
            }
            p.Destroyed = false;
            HomeValleyWorkOrders.CreateConstructionOrder(state, BeltPlanPrefix + p.PlanId, p.ScrapPerCell * UnbuiltCells(p), BeltSecondsPerCell * (IsUnderground(p) ? 2 : 1));
            Revision++;
            return true;
        }

        /// <summary>“全部重建”：每一份被摧毁的传送带虚影都生成施工单（按被摧毁的先后）。返回安排了几份。</summary>
        public static int RebuildAllDestroyed(CampaignState state)
        {
            int n = 0;
            PlannedBeltRecord[] plans = state?.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>();
            foreach (PlannedBeltRecord p in plans)
            {
                if (p != null && p.Destroyed && RebuildDestroyed(state, p.PlanId))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>移除一份被摧毁的传送带虚影（施工队列“取消”；与拆除模式点它同一结果：放开格网，没有材料要退）。</summary>
        public static bool RemoveDestroyedGhost(CampaignState state, string planId)
        {
            PlannedBeltRecord p = FindPlan(state, planId);
            if (p == null || !p.Destroyed)
            {
                return false;
            }
            var cells = new List<GridCell>(p.Xs.Length);
            for (int i = 0; i < p.Xs.Length; i++)
            {
                if (p.CellState[i] == 0)
                {
                    cells.Add(new GridCell(p.Xs[i], p.Ys[i]));
                }
            }
            return CancelPlannedCells(state, cells) > 0;
        }

        /// <summary>现场完成度（0～1）：建筑按施工进度；传送带按已建格数。</summary>
        public static float Fraction(CampaignState state, WorkOrderRecord order)
        {
            if (order == null)
            {
                return 0f;
            }
            if (IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord p = FindPlan(state, order.TargetId);
                if (p == null)
                {
                    return 0f;
                }
                int built = BuiltCells(p);
                int total = built + UnbuiltCells(p);
                return total == 0 ? 1f : (built + Mathf.Clamp01(order.Progress / BeltSecondsPerCell)) / total;
            }
            return order.Duration <= 0f ? 0f : Mathf.Clamp01(order.Progress / order.Duration);
        }

        /// <summary>施工状态的玩家文字（队列、悬停、建造栏共用同一写法，B06：写明原因与办法）。</summary>
        public static string DescribeStatus(CampaignState state, WorkOrderRecord order)
        {
            if (order == null)
            {
                return string.Empty;
            }
            string reason = order.FailureReason;
            if (order.State == WorkOrderState.Waiting && reason != null && reason.StartsWith(MaterialsReasonPrefix, StringComparison.Ordinal))
            {
                int need = MaterialsStillNeeded(state, order);
                return GameText.Format("build.status.waiting_materials", MaterialName(CampaignEconomyLedger.ResourceScrap),
                    need.ToString(CultureInfo.InvariantCulture), Mathf.FloorToInt(state.Scrap).ToString(CultureInfo.InvariantCulture));
            }
            if (order.State == WorkOrderState.Waiting && reason == "path-blocked")
            {
                return GameText.Get("build.status.path_blocked");
            }
            if (HomeValleyWorkOrders.IsUnreachableReason(reason))
            {
                return GameText.Format("nav.work.reason", GameText.Get(Nav.NavService.FailKey(HomeValleyWorkOrders.ParseUnreachable(reason))));
            }
            if ((order.State == WorkOrderState.Ready || order.State == WorkOrderState.Waiting) && NoLabor)
            {
                return GameText.Get("build.status.no_labor");
            }
            if (order.State == WorkOrderState.Ready)
            {
                return GameText.Get("build.status.queued");
            }
            string machine = order.AssignedMachineLogicId > 0 ? Feedback.FeedbackCues.MachineLabel(order.AssignedMachineLogicId) : string.Empty;
            if (order.State == WorkOrderState.Reserved)
            {
                return GameText.Format(order.Leg == 1 ? "build.status.fetching" : "build.status.delivering", machine);
            }
            if (order.State == WorkOrderState.InProgress)
            {
                return GameText.Format("build.status.building", machine, Mathf.RoundToInt(Fraction(state, order) * 100f).ToString(CultureInfo.InvariantCulture));
            }
            return string.Empty;
        }

        public static string PriorityName(int priority)
        {
            switch (Mathf.Clamp(priority, PriorityMin, PriorityMax))
            {
                case -1: return GameText.Get("build.priority.low");
                case 1: return GameText.Get("build.priority.high");
                case 2: return GameText.Get("build.priority.top");
                default: return GameText.Get("build.priority.normal");
            }
        }

        /// <summary>玩家改一张施工单的优先级（低 / 普通 / 高 / 最高）。机器领单时先按机器自己的工作偏好、再按这里、再按先后（ERD-WRK-002）。</summary>
        public static bool SetPriority(CampaignState state, string workOrderId, int priority)
        {
            WorkOrderRecord o = HomeValleyWorkOrders.Find(state, workOrderId);
            if (o == null || o.Kind != WorkOrderKind.Build || !HomeValleyWorkOrders.IsActive(o))
            {
                return false;
            }
            int p = Mathf.Clamp(priority, PriorityMin, PriorityMax);
            if (o.Priority == p)
            {
                return false;
            }
            o.Priority = p;
            HomeValleyWorkOrders.MarkAssignmentDirty();
            Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstPrioritize);
            Revision++;
            return true;
        }

        /// <summary>
        /// “优先建造这一片”：框（闭区间，任意两角）里的施工现场（建筑虚影占地与框相交、传送带规划有未建成的格子在框里）全部改为最高优先级，
        /// 其余不动（不替玩家降低别处）。返回改动的施工单数。O(施工单数 × 规划格数)，只在玩家操作时。
        /// </summary>
        public static int PrioritizeArea(CampaignState state, GridCell a, GridCell b)
        {
            if (state?.WorkOrders == null)
            {
                return 0;
            }
            int minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            int changed = 0;
            foreach (WorkOrderRecord o in state.WorkOrders)
            {
                if (o == null || o.Kind != WorkOrderKind.Build || !HomeValleyWorkOrders.IsActive(o) || o.Priority >= PriorityMax)
                {
                    continue;
                }
                if (!SiteInBox(state, o, minX, maxX, minY, maxY))
                {
                    continue;
                }
                o.Priority = PriorityMax;
                changed++;
            }
            if (changed > 0)
            {
                HomeValleyWorkOrders.MarkAssignmentDirty();
                Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstPrioritize);
                Revision++;
            }
            LastPrioritized = changed;
            return changed;
        }

        /// <summary>
        /// 从施工队列取消一处施工（与拆除模式点虚影同一套规则）：建筑虚影 = 取消规划、已到材料全额退回、占格放开；搬迁虚影 = 取消搬迁；
        /// 传送带规划 = 没建成的格子全部取消、已到材料退回（已经建成的格子是真正的传送带，留着）。返回是否取消了。
        /// </summary>
        public static bool CancelSite(CampaignState state, WorkOrderRecord order)
        {
            if (state == null || order == null || order.Kind != WorkOrderKind.Build || !HomeValleyWorkOrders.IsActive(order))
            {
                return false;
            }
            if (IsBeltPlan(order.TargetId))
            {
                HomeValleyWorkOrders.CancelOrder(state, order.WorkOrderId, SitePosition(state, order));
                return true;
            }
            GridOpResult r = HomeGridService.TryToggleDemolish(state, order.TargetId);
            return r.Outcome == GridOpResult.Kind.PlanCancelled;
        }

        /// <summary>最近一次“优先建造这一片”改动的施工单数。</summary>
        public static int LastPrioritized { get; private set; }

        /// <summary>框里有几个施工现场（拉框时 HUD 显示）。</summary>
        public static int CountSitesInBox(CampaignState state, GridCell a, GridCell b)
        {
            int minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            int n = 0;
            foreach (WorkOrderRecord o in state?.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.Kind == WorkOrderKind.Build && HomeValleyWorkOrders.IsActive(o) && SiteInBox(state, o, minX, maxX, minY, maxY))
                {
                    n++;
                }
            }
            return n;
        }

        private static bool SiteInBox(CampaignState state, WorkOrderRecord o, int minX, int maxX, int minY, int maxY)
        {
            if (IsBeltPlan(o.TargetId))
            {
                PlannedBeltRecord p = FindPlan(state, o.TargetId);
                if (p == null)
                {
                    return false;
                }
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (p.CellState[i] == 0 && p.Xs[i] >= minX && p.Xs[i] <= maxX && p.Ys[i] >= minY && p.Ys[i] <= maxY)
                    {
                        return true;
                    }
                }
                return false;
            }
            BuildingRecord b = FindSiteBuilding(state, o.TargetId);
            if (b == null || !GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g))
            {
                return false;
            }
            GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(b.Rotation),
                out GridCell bMin, out GridCell bMax);
            return !(bMax.X < minX || bMin.X > maxX || bMax.Y < minY || bMin.Y > maxY);
        }

        /// <summary>悬停在虚影 / 规划中的传送带格上时的读数（标题 + 状态 + 材料 + 优先级）；不是施工现场返回 false。</summary>
        public static bool TryDescribeSite(CampaignState state, GridCell cell, out string title, out string body)
        {
            title = null;
            body = null;
            if (state == null)
            {
                return false;
            }
            WorkOrderRecord order = null;
            if (TryFindPlannedCell(state, cell, out PlannedBeltRecord p, out int pi))
            {
                if (p.Destroyed)
                {
                    // FG3-LOG-03：被摧毁的传送带虚影——写明保留的设置与怎么重建 / 移除。
                    title = DestroyedName(p, pi);
                    body = GameText.Format("build.hover.destroyed", PieceName(p), GameText.Get(GridMath.DirTextKey((GridDir)p.Dirs[pi])),
                        InputDisplay.ForAction(GameActionId.ConstructionQueue));
                    return true;
                }
                order = HomeValleyWorkOrders.FindActiveBuild(state, BeltPlanPrefix + p.PlanId);
            }
            else
            {
                BuildingRecord b = HomeGridService.BuildingAt(state, cell);
                if (b != null && HomeValleyController.IsPlannedGhost(b))
                {
                    order = HomeValleyWorkOrders.FindActiveBuild(state, b.BuildingId);
                }
            }
            if (order == null)
            {
                return false;
            }
            SiteMaterials(state, order, out int required, out int delivered);
            title = GameText.Format("build.hover.title", HomeValleyWorkOrders.DescribeTarget(state, order));
            body = DescribeStatus(state, order) + "\n"
                   + GameText.Format("build.hover.materials", MaterialName(CampaignEconomyLedger.ResourceScrap),
                       delivered.ToString(CultureInfo.InvariantCulture), required.ToString(CultureInfo.InvariantCulture),
                       Mathf.RoundToInt(Fraction(state, order) * 100f).ToString(CultureInfo.InvariantCulture)) + "\n"
                   + GameText.Format("build.hover.priority", PriorityName(order.Priority));
            return true;
        }
    }
}
