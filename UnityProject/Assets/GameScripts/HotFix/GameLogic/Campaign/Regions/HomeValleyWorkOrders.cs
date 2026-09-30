using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER3-WRK-01：ERD-WRK-001/003 五类 WorkOrder（Haul/Build/Repair/Salvage/Recharge）的
    /// 唯一状态机实现，取代 ER2-SCENE-01/ER3-STO-01 遗留的"每类各自一套 _xxxRemainingSeconds 内存
    /// 字典 + 到达才补登一条几乎不参与状态机的 WorkOrderRecord"写法。
    ///
    /// ── 订单何时创建 ──
    /// 玩家点选目标那一刻（不是到达那一刻）就创建/复用订单并转 <see cref="WorkOrderState.Reserved"/>——
    /// 与旧写法（到达后才 new 一条 InProgress 记录）不同，机器在赶路途中也真实"持有"一份订单，
    /// 工作面板才有东西可显示，ERD-WRK-003"路径卡住/机器受控/机器死亡"这些中断规则在赶路阶段
    /// 才有意义去打断。消费型资源（Repair/Build）在创建那一刻立即 Reserve（真正扣款）——与
    /// ER3-ECO-01/ER2-SCENE-01 既有 UX 一致："下令那一刻就看到废料减少"，不是走到地方才扣。
    ///
    /// ── Duration/Progress 落盘 ──
    /// <see cref="WorkOrderRecord.Progress"/>/<see cref="WorkOrderRecord.Duration"/>（本 Story 新增字段）
    /// 直接持久化在订单里，真实进程重启不再需要"整段时长重新计时"的兜底（<c>RearmInterruptedWorkOrders</c>/
    /// <c>RearmInterruptedSalvage</c> 那类写法本 Story 起不再需要，读档后 <see cref="Tick"/> 直接从
    /// 上次的 Progress 续走）。
    ///
    /// ── 机器死亡/受控没有真实触发源 ──
    /// 归还谷地当前没有战斗，机器不会真的阵亡；"机器受控"只在 WASD 接管时发生。本类的死亡/受控处理
    /// 规则本身是真实、可独立单测的（<see cref="MachineRegistry.MarkDeadByLogicId"/> 已存在，只是尚无
    /// 归还谷地内的调用方），Play Mode 验收只能覆盖"受控"这一条真实可触发路径，死亡走离线单测直接调用
    /// 该 API 模拟，见 evidence 文档——这是依赖尚不存在（家园内战斗）的诚实范围裁剪，不是遗漏。</summary>
    public static class HomeValleyWorkOrders
    {
        public readonly struct WorkOrderOpResult
        {
            public readonly bool Success;
            public readonly string FailureReason;
            public readonly string WorkOrderId;

            private WorkOrderOpResult(bool success, string failureReason, string workOrderId)
            {
                Success = success;
                FailureReason = failureReason;
                WorkOrderId = workOrderId;
            }

            public static WorkOrderOpResult Ok(string workOrderId) => new WorkOrderOpResult(true, null, workOrderId);
            public static WorkOrderOpResult Fail(string reason) => new WorkOrderOpResult(false, reason, null);
        }

        private const string DestinationCore = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeCore;

        // ── 能力（RequiredTags 的最小落地：Demo 现存机型能力表）───────────────────────
        // ERC-001/002 是通用搬运轮式，"可搬运/建造/拆解/建筑基础维修/编队/直控"（DEMO-CONTENT-LOCK.md
        // §2.1）；ERC-003 是战斗履带，只能给自己 Recharge，不承担家园劳务——它当前不会在归还谷地出生
        // （由 ER4-FAC-01 装配站生产后驶出工厂），这里先落数据，不等它落地才补。

        private static readonly Dictionary<string, WorkOrderKind[]> ChassisCapabilities =
            new Dictionary<string, WorkOrderKind[]>
            {
                [HomeValleyLayout.Erc001ChassisId] = new[]
                {
                    WorkOrderKind.Haul, WorkOrderKind.Build, WorkOrderKind.Repair,
                    WorkOrderKind.Salvage, WorkOrderKind.Recharge,
                },
                [HomeValleyLayout.Erc002ChassisId] = new[]
                {
                    WorkOrderKind.Haul, WorkOrderKind.Build, WorkOrderKind.Repair,
                    WorkOrderKind.Salvage, WorkOrderKind.Recharge,
                },
                [HomeValleyLayout.Erc003ChassisId] = new[] { WorkOrderKind.Recharge },
                // ER3-SOFTLOCK-01：紧急救援机只能 Repair——AC-ECO-011"不能战斗、没有货舱、不能拆废料
                // 或刷生产统计"，只登记这一项能力，天然拒绝 Haul/Build/Salvage/Recharge。
                [HomeValleyLayout.ErcRescueChassisId] = new[] { WorkOrderKind.Repair },
            };

        private static string[] RequiredTagsFor(WorkOrderKind kind) => new[] { kind.ToString().ToLowerInvariant() };

        private static bool IsCapable(string chassisId, WorkOrderKind kind)
        {
            return ChassisCapabilities.TryGetValue(chassisId, out WorkOrderKind[] kinds) && kinds.Contains(kind);
        }

        // ── 查询 ─────────────────────────────────────────────────────────────────

        public static WorkOrderRecord Find(CampaignState state, string workOrderId)
        {
            return state?.WorkOrders?.FirstOrDefault(o => o.WorkOrderId == workOrderId);
        }

        private static bool IsTerminal(WorkOrderState s) =>
            s == WorkOrderState.Completed || s == WorkOrderState.Cancelled || s == WorkOrderState.Failed;

        /// <summary>工单还没结束（待分配 / 已分配 / 进行中 / 等待）。</summary>
        public static bool IsActive(WorkOrderRecord o) => o != null && !IsTerminal(o.State);

        /// <summary>这种底盘能不能做这一类工作（能力表）。</summary>
        public static bool CanDoKind(string chassisId, WorkOrderKind kind) => IsCapable(chassisId, kind);

        /// <summary>FG3-LOG-02：这个现场（建筑 ID 或 "beltplan:…"）还没结束的施工单。</summary>
        public static WorkOrderRecord FindActiveBuild(CampaignState state, string targetId) =>
            state?.WorkOrders == null ? null : FindActiveByTarget(state, WorkOrderKind.Build, targetId);

        /// <summary>还没结束的施工单数（劳动力提示用，O(工单数)）。</summary>
        public static int CountActiveConstruction(CampaignState state)
        {
            int n = 0;
            foreach (WorkOrderRecord o in state?.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.Kind == WorkOrderKind.Build && !IsTerminal(o.State))
                {
                    n++;
                }
            }
            return n;
        }

        private static WorkOrderRecord FindActiveByTarget(CampaignState state, WorkOrderKind kind, string targetId)
        {
            return state.WorkOrders?.LastOrDefault(o => o.Kind == kind && o.TargetId == targetId && !IsTerminal(o.State));
        }

        public static WorkOrderRecord FindActiveOrderForMachine(CampaignState state, int machineLogicId)
        {
            if (machineLogicId <= 0)
            {
                return null;
            }
            return state.WorkOrders?.LastOrDefault(o => o.AssignedMachineLogicId == machineLogicId && !IsTerminal(o.State));
        }

        private static void Append(CampaignState state, WorkOrderRecord order)
        {
            state.WorkOrders = (state.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Append(order).ToArray();
        }

        private static long NowTick(CampaignState state) => (long)(state.PlaySeconds * 1000f);

        // ── 通用创建前置校验 ─────────────────────────────────────────────────────

        private readonly struct MachineCheck
        {
            public readonly bool Ok;
            public readonly string FailureReason;
            public readonly MachineRecord Record;
            private MachineCheck(bool ok, string reason, MachineRecord record) { Ok = ok; FailureReason = reason; Record = record; }
            public static MachineCheck Pass(MachineRecord r) => new MachineCheck(true, null, r);
            public static MachineCheck Bad(string reason) => new MachineCheck(false, reason, null);
        }

        /// <summary>ER8-NEG-01：下令失败（修复/建造/拆解/拆除/充电/搬运）的玩家文字——此前只写日志，玩家点了没反应。
        /// 原因码原样留给逻辑与日志。</summary>
        public static string DescribeCommandFailure(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return "无法执行这项命令";
            }
            string shortfall = CampaignEconomyLedger.DescribeShortfall(reason);
            if (shortfall != null)
            {
                return shortfall + "（拆解残骸、把废料搬进仓库后再试）";
            }
            if (reason.StartsWith("machine-not-capable:", StringComparison.Ordinal))
            {
                string kind = reason.Substring(reason.LastIndexOf(':') + 1);
                return $"这台机器不能{KindVerb(kind)}，换一台搬运机试试";
            }
            if (reason.StartsWith(GridFailurePrefix, StringComparison.Ordinal)) return reason.Substring(GridFailurePrefix.Length);
            if (reason.StartsWith(HomeValleyConstruction.MaterialsReasonPrefix, StringComparison.Ordinal)) return HomeValleyConstruction.DescribeMaterialsReason(reason);
            if (reason.StartsWith(UnsupportedResourcePrefix, StringComparison.Ordinal))
            {
                return Localization.GameText.Format("build.return.cannot_store", HomeValleyConstruction.MaterialName(reason.Substring(UnsupportedResourcePrefix.Length)));
            }
            if (reason.StartsWith("order-already-active", StringComparison.Ordinal)) return "这项工作已经有机器在做";
            if (reason.StartsWith("not-damaged", StringComparison.Ordinal)) return "这座建筑没有损坏，不需要修复";
            if (reason.StartsWith("no-repair-profile", StringComparison.Ordinal)) return "这座建筑不能修复";
            if (reason.StartsWith("no-build-profile", StringComparison.Ordinal)) return "这里不能建造";
            if (reason.StartsWith("not-operational", StringComparison.Ordinal)) return "只有运转中的建筑才能拆除";
            if (reason.StartsWith("building-not-found", StringComparison.Ordinal)
                || reason.StartsWith("ground-item-not-found", StringComparison.Ordinal)
                || reason.StartsWith("not-a-wreckage-node", StringComparison.Ordinal)
                || reason == "region-not-found")
            {
                return "目标已不存在";
            }
            switch (reason)
            {
                case "machine-not-found-or-dead": return "这台机器已不在场";
                case "machine-out-of-region": return "这台机器不在归还谷地";
                case "already-built": return "这里已经建好了";
                case "already-salvaged": return "这处残骸已经拆完";
                case "battery-already-full": return "电量已满，不需要充电";
                case "cannot-demolish-core": return "归还核心不能拆除";
                case "not-rebuildable": return Localization.GameText.Get("grid.reason.not_rebuildable");
                case "not-rescue-machine": return "只有紧急救援机能做紧急修复";
                default: return "无法执行这项命令";
            }
        }

        private static string KindVerb(string kind)
        {
            switch (kind)
            {
                case nameof(WorkOrderKind.Haul): return "搬运";
                case nameof(WorkOrderKind.Build): return "建造";
                case nameof(WorkOrderKind.Repair): return "修复";
                case nameof(WorkOrderKind.Salvage): return "拆解";
                case nameof(WorkOrderKind.Recharge): return "给别的建筑充电";
                default: return "做这项工作";
            }
        }

        private static MachineCheck CheckMachine(int machineLogicId, WorkOrderKind kind)
        {
            if (!MachineRegistry.TryGetRecord(machineLogicId, out MachineRecord record) || !record.IsAlive)
            {
                return MachineCheck.Bad("machine-not-found-or-dead");
            }
            if (record.RegionId != HomeValleyLayout.RegionId)
            {
                return MachineCheck.Bad("machine-out-of-region");
            }
            if (!IsCapable(record.ChassisId, kind))
            {
                return MachineCheck.Bad($"machine-not-capable:{record.ChassisId}:{kind}");
            }
            return MachineCheck.Pass(record);
        }

        // ── Repair ───────────────────────────────────────────────────────────────

        public static WorkOrderOpResult TryCreateRepair(CampaignState state, string buildingTypeId, int machineLogicId)
        {
            // FG3-LOG-06：同类建筑有多座时（电塔）优先修受损的那一座（修复工单仍按类型下单；按实例指定修哪一座见 FG-GAP-084 → FG6-DEF-05）。
            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b =>
                                          b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == buildingTypeId && b.ConstructionState == BuildingConstructionState.Damaged)
                                      ?? state.BuildingRecords?.FirstOrDefault(b =>
                                          b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == buildingTypeId);
            if (building == null)
            {
                return WorkOrderOpResult.Fail($"building-not-found:{buildingTypeId}");
            }

            WorkOrderRecord existing = FindActiveByTarget(state, WorkOrderKind.Repair, building.BuildingId);
            if (existing != null)
            {
                if (existing.State != WorkOrderState.Ready)
                {
                    return WorkOrderOpResult.Fail($"order-already-active:{existing.State}");
                }
                return ReassignExisting(state, existing, machineLogicId);
            }

            if (building.ConstructionState != BuildingConstructionState.Damaged)
            {
                return WorkOrderOpResult.Fail($"not-damaged:{building.ConstructionState}");
            }
            if (!HomeValleyLayout.RepairProfile.TryGetValue(buildingTypeId, out (int ScrapCost, float Seconds) profile))
            {
                return WorkOrderOpResult.Fail($"no-repair-profile:{buildingTypeId}");
            }

            MachineCheck check = CheckMachine(machineLogicId, WorkOrderKind.Repair);
            if (!check.Ok)
            {
                return WorkOrderOpResult.Fail(check.FailureReason);
            }

            string workOrderId = building.BuildingId + ":repair:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string txId = workOrderId + ":tx";
            CampaignEconomyLedger.ProposeConsume(state, txId, building.BuildingId, CampaignEconomyLedger.ResourceScrap, profile.ScrapCost);
            CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, txId);
            if (!reserve.Success)
            {
                CampaignEconomyLedger.Cancel(state, txId);
                return WorkOrderOpResult.Fail($"insufficient-scrap:{reserve.FailureReason}");
            }

            var order = NewOrder(state, workOrderId, WorkOrderKind.Repair, building.BuildingId, machineLogicId,
                resourceTransactionId: txId, duration: profile.Seconds);
            Append(state, order);
            return WorkOrderOpResult.Ok(workOrderId);
        }

        /// <summary>ER3-SOFTLOCK-01 AC-ECO-011"核心紧急重启搬运机…执行建筑修复"——紧急救援机专属的
        /// 免费修复：不走 <see cref="CampaignEconomyLedger"/>（<c>resourceTransactionId</c> 恒为
        /// null），因为这条机制存在的唯一理由就是"玩家连废料都拿不出手"（触发阈值本身是废料&lt;35，
        /// 极端情况下是 0）——如果紧急机自己的修复还要正常收费，AC-ECO-011 描述的"核心紧急重启"
        /// 在废料归零时会变得不可能打破死锁，与卡片"从正式 UI 恢复供电和可生产状态"的意图矛盾。
        /// 只允许 <see cref="HomeValleyLayout.ErcRescueChassisId"/> 调用，避免正式搬运机也走这条
        /// 免费捷径绕过正常经济。<see cref="CompleteRepair"/> 对 null 事务 id 安全跳过 Commit。</summary>
        public static WorkOrderOpResult TryCreateEmergencyRepair(CampaignState state, string buildingTypeId, int machineLogicId)
        {
            if (!MachineRegistry.TryGetRecord(machineLogicId, out MachineRecord record) || !record.IsAlive
                || record.ChassisId != HomeValleyLayout.ErcRescueChassisId)
            {
                return WorkOrderOpResult.Fail("not-rescue-machine");
            }

            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == buildingTypeId);
            if (building == null)
            {
                return WorkOrderOpResult.Fail($"building-not-found:{buildingTypeId}");
            }

            WorkOrderRecord existing = FindActiveByTarget(state, WorkOrderKind.Repair, building.BuildingId);
            if (existing != null)
            {
                if (existing.State != WorkOrderState.Ready)
                {
                    return WorkOrderOpResult.Fail($"order-already-active:{existing.State}");
                }
                return ReassignExisting(state, existing, machineLogicId);
            }

            if (building.ConstructionState != BuildingConstructionState.Damaged)
            {
                return WorkOrderOpResult.Fail($"not-damaged:{building.ConstructionState}");
            }
            if (!HomeValleyLayout.RepairProfile.TryGetValue(buildingTypeId, out (int ScrapCost, float Seconds) profile))
            {
                return WorkOrderOpResult.Fail($"no-repair-profile:{buildingTypeId}");
            }

            string workOrderId = building.BuildingId + ":emergency-repair:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var order = NewOrder(state, workOrderId, WorkOrderKind.Repair, building.BuildingId, machineLogicId,
                resourceTransactionId: null, duration: profile.Seconds);
            Append(state, order);
            return WorkOrderOpResult.Ok(workOrderId);
        }

        // ── Build（FG0-ARCH-04 起一律经格网放置：HomeGridService.TryPlace → TryCreateBuildAt）─────────────

        /// <summary>Demo 建造位点选路径（机器选中后点“建造位”）：<paramref name="siteOrTypeId"/> 是开局布局里的建造位锚点
        /// （generator_2 / beacon_slot）或建造位的建筑类型。FG0-ARCH-04 起建造位只是“建议位置”，放置照样经过格网校验
        /// （被别的建筑占了、在迷雾里……都会给出原因），不再有第二套建造入口。</summary>
        public static WorkOrderOpResult TryCreateBuild(CampaignState state, string siteOrTypeId, int machineLogicId)
        {
            if (!TryResolveSite(siteOrTypeId, out GameConfig.fg.StartLayout site))
            {
                return WorkOrderOpResult.Fail($"no-build-profile:{siteOrTypeId}");
            }
            string plannedBuildingId = HomeValleyLayout.RegionId + ":" + site.TypeId;
            WorkOrderRecord existing = FindActiveByTarget(state, WorkOrderKind.Build, plannedBuildingId);
            if (existing != null)
            {
                if (existing.State != WorkOrderState.Ready)
                {
                    return WorkOrderOpResult.Fail($"order-already-active:{existing.State}");
                }
                return ReassignExisting(state, existing, machineLogicId);
            }
            if (state.BuildingRecords?.Any(b => b.BuildingId == plannedBuildingId) ?? false)
            {
                return WorkOrderOpResult.Fail("already-built");
            }
            MachineCheck check = CheckMachine(machineLogicId, WorkOrderKind.Build);
            if (!check.Ok)
            {
                return WorkOrderOpResult.Fail(check.FailureReason);
            }
            Grid.GridCell cell = Grid.HomeGridService.AnchorCell(state, site.AnchorId);
            Grid.GridOpResult placed = Grid.HomeGridService.TryPlace(state, site.TypeId, cell, site.Rotation, machineLogicId);
            if (!placed.Success)
            {
                return WorkOrderOpResult.Fail(GridFailurePrefix + placed.Describe());
            }
            WorkOrderRecord order = FindActiveByTarget(state, WorkOrderKind.Build, placed.BuildingId);
            return order != null ? WorkOrderOpResult.Ok(order.WorkOrderId) : WorkOrderOpResult.Fail("order-missing");
        }

        /// <summary>格网放置被拒时的原因码前缀；后面跟当前语言的原因文本，<see cref="DescribeCommandFailure"/> 原样显示。</summary>
        public const string GridFailurePrefix = "grid:";

        /// <summary>FG3-LOG-02：家园仓库存不了的资源类型（后面跟资源类型），搬运被拒 / 失败时的原因码前缀。</summary>
        public const string UnsupportedResourcePrefix = "unsupported-resource-type:";

        private static bool TryResolveSite(string siteOrTypeId, out GameConfig.fg.StartLayout site)
        {
            site = null;
            if (siteOrTypeId == null)
            {
                return false;
            }
            if (Grid.GridContent.TryGetLayout(siteOrTypeId, out GameConfig.fg.StartLayout row) && row.Kind == "site")
            {
                site = row;
                return true;
            }
            foreach (GameConfig.fg.StartLayout r in Grid.GridContent.StartLayout)
            {
                if (r.Kind == "site" && r.TypeId == siteOrTypeId)
                {
                    site = r;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// FG0-ARCH-04：在格网上已经校验过的位置生成“规划中”的建筑记录（虚影）与施工单（唯一调用方 <see cref="Grid.HomeGridService.TryPlace"/>）。
        /// FG3-LOG-02（FGR-LOG-006）：放置时**不扣材料**（库存不够也能放）——虚影记下所需材料，施工单第一腿是“去仓库取料”，
        /// 机器取到多少运多少，进度不超过已到的材料（<see cref="HomeValleyConstruction"/>）；取消时已到现场的材料全额退回。
        /// <paramref name="machineLogicId"/> = 0 时工作单进入待分配池（Ready），由空闲机器按 ERD-WRK-002 自动领取。
        /// </summary>
        public static WorkOrderOpResult TryCreateBuildAt(CampaignState state, string buildingTypeId, string buildingId,
            Grid.GridCell pivot, int rotation, Vector2 center, int machineLogicId)
        {
            if (!HomeValleyLayout.BuildProfile.TryGetValue(buildingTypeId, out (int ScrapCost, float Seconds) profile))
            {
                return WorkOrderOpResult.Fail($"no-build-profile:{buildingTypeId}");
            }
            if (state.BuildingRecords?.Any(b => b.BuildingId == buildingId) ?? false)
            {
                return WorkOrderOpResult.Fail("already-built");
            }
            if (machineLogicId != 0)
            {
                MachineCheck check = CheckMachine(machineLogicId, WorkOrderKind.Build);
                if (!check.Ok)
                {
                    return WorkOrderOpResult.Fail(check.FailureReason);
                }
            }

            string workOrderId = buildingId + ":build:" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var planned = new BuildingRecord
            {
                BuildingId = buildingId,
                BuildingTypeId = buildingTypeId,
                RegionId = HomeValleyLayout.RegionId,
                Position = center,
                Rotation = Grid.GridMath.NormalizeRotation(rotation),
                GridX = pivot.X,
                GridY = pivot.Y,
                Health = 100f,
                ConstructionState = BuildingConstructionState.Planned,
                // ER7-BEACON-01：按 PowerProfile 默认优先级接入电网仲裁，查不到则退化 1。
                PowerPriority = HomeValleyLayout.PowerProfile.TryGetValue(buildingTypeId, out (float, int) profileEntry)
                    ? profileEntry.Item2
                    : 1,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
                BlockedReason = null,
                ConstructionRequired = Math.Max(0, profile.ScrapCost),
                ConstructionDelivered = 0,
            };
            state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(planned).ToArray();

            var order = NewOrder(state, workOrderId, WorkOrderKind.Build, buildingId, machineLogicId,
                resourceTransactionId: null, duration: profile.Seconds);
            order.Leg = planned.ConstructionRequired > 0 ? 1 : 0;
            if (machineLogicId == 0)
            {
                order.State = WorkOrderState.Ready; // 待分配池：空闲机器 0.5 秒内领取（ERD-WRK-002）。
            }
            Append(state, order);
            MarkAssignmentDirty();
            HomeValleyConstruction.Touch();
            return WorkOrderOpResult.Ok(workOrderId);
        }

        /// <summary>FG3-LOG-02：传送带规划的施工单（<see cref="HomeValleyConstruction.PlanBelts"/> 调用）：待分配池，第一腿去仓库取料（单价为 0 时直接去现场）。</summary>
        internal static WorkOrderRecord CreateConstructionOrder(CampaignState state, string targetId, int required, float duration)
        {
            string workOrderId = targetId + ":build:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WorkOrderRecord order = NewOrder(state, workOrderId, WorkOrderKind.Build, targetId, 0, resourceTransactionId: null, duration: duration);
            order.Leg = required > 0 ? 1 : 0;
            order.State = WorkOrderState.Ready;
            Append(state, order);
            MarkAssignmentDirty();
            return order;
        }

        /// <summary>FG3-LOG-02：施工现场被摧毁后，施工单回到待分配池、进度归零（正在干活的机器放下这单，货舱里的材料退回仓库）。</summary>
        internal static void ResetForRebuild(CampaignState state, WorkOrderRecord order)
        {
            if (order == null || IsTerminal(order.State))
            {
                return;
            }
            PathWatch.Remove(order.WorkOrderId);
            WaitingWatch.Remove(order.WorkOrderId);
            ReleaseBuildMachine(state, order, dropOnly: false);
            order.Progress = 0f;
            order.Leg = HomeValleyConstruction.MaterialsStillNeeded(state, order) > 0 ? 1 : 0;
            order.State = WorkOrderState.Ready;
            order.FailureReason = null;
            order.AssignedMachineLogicId = 0;
            MarkAssignmentDirty();
        }

        /// <summary>FG3-LOG-02：施工单放掉机器前，把机器货舱里这一趟的施工材料退回（机器阵亡时就地落地）。</summary>
        private static void ReleaseBuildMachine(CampaignState state, WorkOrderRecord order, bool dropOnly)
        {
            if (order.Kind != WorkOrderKind.Build || order.AssignedMachineLogicId <= 0
                || !MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord m))
            {
                return;
            }
            HomeValleyConstruction.ReleaseCargo(state, order, m, dropOnly);
        }

        // ── FG3-LOG-02：返还物的搬运单（仓库满时返还变成地面物，“由机器之后搬运”，FGR-LOG-007）──────────────────

        /// <summary>
        /// 给一份返还的地面物生成搬运单，进入待分配池：仓库有空间时由空闲机器（搬运偏好 &gt; 0）领取，搬回归还核心；
        /// 仓库满时搬运单等待（<see cref="HomeValleyConstruction.ReturnWaitReason"/>，不占机器），腾出空间自动回到待分配池。
        /// 同一份地面物只生成一张。只用于本系统产生的返还（拆除、取消、被摧毁），不会替玩家去搬别的地面物（FGR-BASE-020）。
        /// </summary>
        public static WorkOrderOpResult TryCreateHaulPool(CampaignState state, string groundItemId)
        {
            if (state == null || string.IsNullOrEmpty(groundItemId))
            {
                return WorkOrderOpResult.Fail("invalid-args");
            }
            WorkOrderRecord existing = FindActiveByTarget(state, WorkOrderKind.Haul, groundItemId);
            if (existing != null)
            {
                return WorkOrderOpResult.Ok(existing.WorkOrderId);
            }
            if (HomeValleyCargo.FindGroundItem(state, groundItemId) == null)
            {
                return WorkOrderOpResult.Fail($"ground-item-not-found:{groundItemId}");
            }
            string workOrderId = groundItemId + ":haul:auto";
            if (Find(state, workOrderId) != null)
            {
                workOrderId += ":" + (state.WorkOrders?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            var order = NewOrder(state, workOrderId, WorkOrderKind.Haul, groundItemId, 0, resourceTransactionId: null, duration: 0f);
            order.SourceId = groundItemId;
            order.DestinationId = DestinationCore;
            order.IssuerId = "return";
            order.State = WorkOrderState.Ready;
            Append(state, order);
            MarkAssignmentDirty();
            return WorkOrderOpResult.Ok(workOrderId);
        }

        /// <summary>
        /// FG3-LOG-01（FGR-LOG-008 搬迁）：在新位置生成“搬迁目标”虚影与一张施工工作单（唯一调用方 <see cref="Grid.HomeGridService.TryRelocate"/>）。
        /// 不花材料（材料就是原建筑本身），工期 <paramref name="seconds"/>；工作单进入待分配池，由空闲机器领取。
        /// 虚影带上原建筑的电力优先级；完工时由 <see cref="CompleteBuild"/> 把原建筑换到新位置（见 <see cref="CompleteRelocation"/>）。
        /// </summary>
        public static WorkOrderOpResult TryCreateRelocationAt(CampaignState state, BuildingRecord source, string ghostId,
            Grid.GridCell pivot, int rotation, Vector2 center, float seconds)
        {
            if (source == null)
            {
                return WorkOrderOpResult.Fail("building-not-found");
            }
            if (state.BuildingRecords?.Any(b => b.BuildingId == ghostId) ?? false)
            {
                return WorkOrderOpResult.Fail("already-relocating");
            }
            var ghost = new BuildingRecord
            {
                BuildingId = ghostId,
                BuildingTypeId = source.BuildingTypeId,
                RegionId = HomeValleyLayout.RegionId,
                Position = center,
                Rotation = Grid.GridMath.NormalizeRotation(rotation),
                GridX = pivot.X,
                GridY = pivot.Y,
                Health = source.Health,
                ConstructionState = BuildingConstructionState.Planned,
                PowerPriority = source.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
                BlockedReason = null,
                RelocateFromId = source.BuildingId,
            };
            state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(ghost).ToArray();
            string workOrderId = ghostId + ":build:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var order = NewOrder(state, workOrderId, WorkOrderKind.Build, ghostId, 0, resourceTransactionId: null, duration: Mathf.Max(0.1f, seconds));
            order.State = WorkOrderState.Ready;
            Append(state, order);
            MarkAssignmentDirty();
            return WorkOrderOpResult.Ok(workOrderId);
        }

        /// <summary>
        /// FG3-LOG-07（FGR-LOG-010 升级规划）：在原位生成“升级目标”虚影（新类型、同一枢轴格与朝向）与一张施工单（唯一调用方 <see cref="Grid.HomeGridService.TryUpgrade"/>）。
        /// 沿用搬迁的“完工那一刻原子换位”：虚影与原建筑占地完全重叠（占格让给原建筑），完工前原建筑照常运转；
        /// 施工要的材料 = 新旧造价的差额（<paramref name="diff"/>），机器从仓库取料送到现场（与普通虚影同一套取料腿）；完工时原建筑换成新类型，
        /// 保留 ID、生命、运行状态、库存、队列、电力优先级，投入 = 原投入 + 差额（拆除时全额返还）。取消 = 已到的差额材料全额退回，原建筑不受影响。
        /// </summary>
        public static WorkOrderOpResult TryCreateUpgradeAt(CampaignState state, BuildingRecord source, string ghostId, string toTypeId, int diff, float seconds)
        {
            if (source == null)
            {
                return WorkOrderOpResult.Fail("building-not-found");
            }
            if (state.BuildingRecords?.Any(b => b.BuildingId == ghostId) ?? false)
            {
                return WorkOrderOpResult.Fail("already-upgrading");
            }
            var ghost = new BuildingRecord
            {
                BuildingId = ghostId,
                BuildingTypeId = toTypeId,
                RegionId = HomeValleyLayout.RegionId,
                Position = source.Position,
                Rotation = source.Rotation,
                GridX = source.GridX,
                GridY = source.GridY,
                Health = source.Health,
                ConstructionState = BuildingConstructionState.Planned,
                PowerPriority = source.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
                BlockedReason = null,
                RelocateFromId = source.BuildingId,
                ConstructionRequired = Math.Max(0, diff),
                ConstructionDelivered = 0,
            };
            state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(ghost).ToArray();
            string workOrderId = ghostId + ":build:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var order = NewOrder(state, workOrderId, WorkOrderKind.Build, ghostId, 0, resourceTransactionId: null, duration: Mathf.Max(0.1f, seconds));
            order.Leg = ghost.ConstructionRequired > 0 ? 1 : 0;
            order.State = WorkOrderState.Ready;
            Append(state, order);
            MarkAssignmentDirty();
            HomeValleyConstruction.Touch();
            return WorkOrderOpResult.Ok(workOrderId);
        }

        /// <summary>FG3-LOG-01：还没开工的规划被挪到新位置——正在赶路的机器放下这单（回待分配池），按新位置重新分配；
        /// 旧的到达回调因为工单不再是“已分配”而失效（<see cref="OnArrivedAtWork"/> 只认 Reserved）。
        /// 原先领单的机器同时收到停止移动（与路径受阻放单一致）：本方法由格网服务调用、拿不到调用方的移动委托，
        /// 所以先记下机器，下一次 <see cref="Tick"/> 开头、分配之前统一发停止——免得它白跑到已经空了的旧位置。</summary>
        public static void OnPlanMoved(CampaignState state, WorkOrderRecord order)
        {
            if (order == null || order.State != WorkOrderState.Reserved)
            {
                return;
            }
            PathWatch.Remove(order.WorkOrderId);
            if (order.AssignedMachineLogicId > 0 && !PendingMovementRelease.Contains(order.AssignedMachineLogicId))
            {
                PendingMovementRelease.Add(order.AssignedMachineLogicId);
            }
            ReleaseBuildMachine(state, order, dropOnly: false); // FG3-LOG-02：货舱里正运往旧位置的材料退回仓库。
            order.Leg = HomeValleyConstruction.MaterialsStillNeeded(state, order) > 0 ? 1 : 0;
            order.State = WorkOrderState.Ready;
            order.AssignedMachineLogicId = 0;
            MarkAssignmentDirty();
        }

        /// <summary>规划被挪走后待停下的机器（上限 = 同一帧内被挪动的已分配规划数，个位数；每次 Tick 开头清空）。</summary>
        private static readonly List<int> PendingMovementRelease = new List<int>(4);

        /// <summary>对 <see cref="OnPlanMoved"/> 记下的机器发停止移动：只停此刻没有别的活跃工单的机器
        /// （同一帧里已被重新分配或被玩家接管的不动）。</summary>
        private static void DrainPendingMovementRelease(CampaignState state, Action<int> releaseMachineMovement)
        {
            if (PendingMovementRelease.Count == 0)
            {
                return;
            }
            for (int i = 0; i < PendingMovementRelease.Count; i++)
            {
                int id = PendingMovementRelease[i];
                if (FindActiveOrderForMachine(state, id) == null)
                {
                    releaseMachineMovement?.Invoke(id);
                }
            }
            PendingMovementRelease.Clear();
        }

        /// <summary>自检用：还有多少台机器等着被停下。</summary>
        public static int PendingMovementReleaseCount => PendingMovementRelease.Count;

        // ── Salvage（拆解残骸，保持 ER3-STO-01 已验证的落地/收货行为）────────────────

        public static WorkOrderOpResult TryCreateSalvage(CampaignState state, string nodeId, int machineLogicId)
        {
            if (nodeId != HomeValleyLayout.Wreckage1NodeId && nodeId != HomeValleyLayout.Wreckage2NodeId)
            {
                return WorkOrderOpResult.Fail($"not-a-wreckage-node:{nodeId}");
            }
            RegionRecord region = state.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId);
            if (region == null)
            {
                return WorkOrderOpResult.Fail("region-not-found");
            }

            WorkOrderRecord existing = FindActiveByTarget(state, WorkOrderKind.Salvage, nodeId);
            if (existing != null)
            {
                if (existing.State != WorkOrderState.Ready)
                {
                    return WorkOrderOpResult.Fail($"order-already-active:{existing.State}");
                }
                return ReassignExisting(state, existing, machineLogicId);
            }

            if (region.DestroyedNodeIds != null && region.DestroyedNodeIds.Contains(nodeId))
            {
                return WorkOrderOpResult.Fail("already-salvaged");
            }

            MachineCheck check = CheckMachine(machineLogicId, WorkOrderKind.Salvage);
            if (!check.Ok)
            {
                return WorkOrderOpResult.Fail(check.FailureReason);
            }

            string workOrderId = nodeId + ":salvage:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string txId = nodeId + ":salvage-tx"; // 沿用 ER3-STO-01 的固定派生 id：一个残骸只拆一次，天然幂等。
            CampaignEconomyLedger.ProposeProduce(state, txId, nodeId, CampaignEconomyLedger.ResourceScrap, HomeValleyLayout.WreckageScrapYield);
            CampaignEconomyLedger.Reserve(state, txId);

            var order = NewOrder(state, workOrderId, WorkOrderKind.Salvage, nodeId, machineLogicId,
                resourceTransactionId: txId, duration: HomeValleyLayout.WreckageDismantleSeconds);
            Append(state, order);
            return WorkOrderOpResult.Ok(workOrderId);
        }

        // ── Demolish（ER3-SOFTLOCK-01 AC-ECO-012：拆除非核心建筑，按实际投入50%返还）──────
        // 刻意复用 WorkOrderKind.Salvage 而不新增枚举值——ERD-WRK-001 把 kind 定义为"封闭"集合
        // （Haul/Build/Repair/Salvage/Recharge），"拆解"本来就是这份契约里最贴切的语义；
        // CompleteSalvage 按 TargetId 是残骸节点还是 BuildingId 分流到两套完成逻辑，工作面板/
        // 看门狗/分配引擎全部免费直接复用，不需要为一个新 kind 再铺一遍状态机。

        public static WorkOrderOpResult TryCreateDemolish(CampaignState state, string buildingTypeId, int machineLogicId)
        {
            if (buildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                return WorkOrderOpResult.Fail("cannot-demolish-core");
            }
            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == buildingTypeId);
            if (building == null)
            {
                return WorkOrderOpResult.Fail($"building-not-found:{buildingTypeId}");
            }
            return TryCreateDemolishBuilding(state, building.BuildingId, machineLogicId);
        }

        /// <summary>FG0-ARCH-04：按建筑 ID 拆除（同类建筑可以有多座，按类型找会拆错那一座）。
        /// <paramref name="machineLogicId"/> = 0：拆除模式下的“标记拆除”，工作单进入待分配池由空闲机器领取。</summary>
        public static WorkOrderOpResult TryCreateDemolishBuilding(CampaignState state, string buildingId, int machineLogicId)
        {
            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingId == buildingId);
            if (building == null)
            {
                return WorkOrderOpResult.Fail($"building-not-found:{buildingId}");
            }
            if (building.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                return WorkOrderOpResult.Fail("cannot-demolish-core");
            }
            if (Grid.HomeGridService.IsDemolishForbidden(building.BuildingTypeId))
            {
                // FG0-ARCH-04（FG00 B11）：本局无法重建的开局建筑（placeable=0）拆了会让引用它的目标永远完不成，战役软锁。
                // 拆除模式与 Demo 的“点选机器再点建筑”两条入口都在这里拦住。
                return WorkOrderOpResult.Fail("not-rebuildable");
            }

            WorkOrderRecord existing = FindActiveByTarget(state, WorkOrderKind.Salvage, building.BuildingId);
            if (existing != null)
            {
                if (existing.State != WorkOrderState.Ready || machineLogicId == 0)
                {
                    return WorkOrderOpResult.Fail($"order-already-active:{existing.State}");
                }
                return ReassignExisting(state, existing, machineLogicId);
            }

            if (building.ConstructionState != BuildingConstructionState.Operational)
            {
                // Damaged 建筑走 Repair（还没真的建成/修复过，谈不上"实际投入"可以拆返）；
                // Planned/Building/Disabled/Destroyed 同样不是合法拆除目标。
                return WorkOrderOpResult.Fail($"not-operational:{building.ConstructionState}");
            }

            if (machineLogicId != 0)
            {
                MachineCheck check = CheckMachine(machineLogicId, WorkOrderKind.Salvage);
                if (!check.Ok)
                {
                    return WorkOrderOpResult.Fail(check.FailureReason);
                }
            }

            string workOrderId = building.BuildingId + ":demolish:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            // FG3-LOG-02（FGR-LOG-007“全额返还”；DEBT-FG0ARCH04-02 关闭）：返还在完工那一刻按建筑实际投入的全额 + 内部缓存结算
            // （<see cref="CompleteDemolish"/>），不再预登记“一半”的生产事务。开局即运转、从没花过材料的建筑投入为 0，返还 0（忠于“实际投入”）。
            var order = NewOrder(state, workOrderId, WorkOrderKind.Salvage, building.BuildingId, machineLogicId,
                resourceTransactionId: null, duration: HomeValleyLayout.DemolishSeconds);
            if (machineLogicId == 0)
            {
                order.State = WorkOrderState.Ready;
            }
            Append(state, order);
            MarkAssignmentDirty();
            return WorkOrderOpResult.Ok(workOrderId);
        }

        // ── Haul（地面物两阶段搬运，复用 HomeValleyCargo 的票据 API）──────────────────

        public static WorkOrderOpResult TryCreateHaul(CampaignState state, string groundItemId, int machineLogicId)
        {
            WorkOrderRecord existing = FindActiveByTarget(state, WorkOrderKind.Haul, groundItemId);
            if (existing != null)
            {
                if (existing.State != WorkOrderState.Ready)
                {
                    return WorkOrderOpResult.Fail($"order-already-active:{existing.State}");
                }
                return ReassignExisting(state, existing, machineLogicId);
            }

            GroundItemRecord item = HomeValleyCargo.FindGroundItem(state, groundItemId);
            if (item == null)
            {
                return WorkOrderOpResult.Fail($"ground-item-not-found:{groundItemId}");
            }
            if (!HomeValleyCargo.CanStore(item.ResourceType))
            {
                // FG3-LOG-02（DEBT-FG3LOG02-01）：家园仓库目前只存废料；返还落地的物品（传送带物品、建筑缓存）搬回去也放不进，
                // 不派一趟必然失败的搬运（此前交付时物品会从货舱里消失）。物品表接入（FG4-ECO-01）后这里放开。
                return WorkOrderOpResult.Fail(UnsupportedResourcePrefix + item.ResourceType);
            }

            MachineCheck check = CheckMachine(machineLogicId, WorkOrderKind.Haul);
            if (!check.Ok)
            {
                return WorkOrderOpResult.Fail(check.FailureReason);
            }

            string workOrderId = groundItemId + ":haul:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var order = NewOrder(state, workOrderId, WorkOrderKind.Haul, groundItemId, machineLogicId,
                resourceTransactionId: null, duration: 0f);
            order.SourceId = groundItemId;
            order.DestinationId = DestinationCore;
            Append(state, order);
            return WorkOrderOpResult.Ok(workOrderId);
        }

        /// <summary>第一腿到达（地面物所在位置）：拾取——移出地面、镜像进机器货舱
        /// （<see cref="MachineRecord.Cargo"/>，ER3-STO-01 预留但未强制使用的字段，本 Story 起真正
        /// 承载"搬运在途"的持久化状态，跨帧/跨进程重启都能正确复原，不再需要临时票据）。</summary>
        public static bool OnArrivedAtHaulSource(CampaignState state, string workOrderId)
        {
            WorkOrderRecord order = Find(state, workOrderId);
            if (order == null || order.Kind != WorkOrderKind.Haul || order.State != WorkOrderState.Reserved)
            {
                return false;
            }

            PathWatch.Remove(order.WorkOrderId); // 离开 Reserved（第一腿赶路结束）。

            HomeValleyCargo.HaulTicket ticket = HomeValleyCargo.TryReserveHaul(state, order.SourceId);
            if (ticket == null)
            {
                // 地面物在赶路途中消失（另一动作已收走）——目标已毁，取消订单，机器空手返回战略层。
                order.State = WorkOrderState.Cancelled;
                order.FailureReason = "source-vanished";
                order.AssignedMachineLogicId = 0;
                MarkAssignmentDirty(); // 机器立即空出来，ER3-WRK-02 分配引擎不必等满 0.5 秒窗口。
                return false;
            }

            if (MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord machine))
            {
                machine.Cargo = new[] { new CargoEntry { ResourceType = ticket.ResourceType, Amount = ticket.Amount } };
            }

            order.State = WorkOrderState.InProgress; // 第二腿：搬运在途，货已在机器货舱里。
            return true;
        }

        /// <summary>第二腿到达（交付点，固定为归还核心）：交付；仓满则 Waiting/storage-full，货物
        /// 留在机器货舱（不落地、不丢失），<see cref="Tick"/> 每帧重试直到腾出空间或玩家取消。</summary>
        public static void OnArrivedAtHaulDestination(CampaignState state, string workOrderId)
        {
            WorkOrderRecord order = Find(state, workOrderId);
            if (order == null || order.Kind != WorkOrderKind.Haul || order.State != WorkOrderState.InProgress)
            {
                return;
            }
            TryDeliverHaul(state, order);
        }

        /// <summary>交付前自己先查容量——不能直接靠 <see cref="HomeValleyCargo.CommitHaul"/> 判定失败，
        /// 那个方法失败时会把物品放回地面（服务"拾取即交付"的旧单帧流程，见其文档），而这里的货物
        /// 一直稳定持有在 <see cref="MachineRecord.Cargo"/>——两边都保留会变成"货舱和地面各一份"的
        /// 真实复制 bug（实测踩到过）。仓满就地留在货舱，不调用 CommitHaul，不产生地面物副本。</summary>
        private static void TryDeliverHaul(CampaignState state, WorkOrderRecord order)
        {
            if (!MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord machine)
                || machine.Cargo == null || machine.Cargo.Length == 0)
            {
                order.State = WorkOrderState.Failed;
                order.FailureReason = "cargo-lost";
                MarkAssignmentDirty();
                return;
            }

            CargoEntry cargo = machine.Cargo[0];
            if (!HomeValleyCargo.CanStore(cargo.ResourceType))
            {
                // 仓库存不了的物品（旧存档或别的入口带进货舱的）：放回机器脚下的地面，不从货舱里凭空消失。
                HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, machine.WorldPosition, cargo.ResourceType, cargo.Amount,
                    order.SourceId + ":redrop:" + order.WorkOrderId);
                machine.Cargo = Array.Empty<CargoEntry>();
                order.State = WorkOrderState.Failed;
                order.FailureReason = UnsupportedResourcePrefix + cargo.ResourceType;
                MarkAssignmentDirty();
                return;
            }
            int available = HomeValleyCargo.GetAvailableSpace(state, cargo.ResourceType);
            if (available < cargo.Amount)
            {
                order.State = WorkOrderState.Waiting;
                order.FailureReason = $"storage-full:need={cargo.Amount}:have={available}";
                return;
            }

            var ticket = new HomeValleyCargo.HaulTicket
            {
                ResourceType = cargo.ResourceType,
                Amount = cargo.Amount,
                SalvageInstanceId = order.SourceId,
                SourcePosition = machine.WorldPosition,
            };
            HomeValleyCargo.StoreResult delivered = HomeValleyCargo.CommitHaul(state, ticket);
            if (!delivered.Success)
            {
                // 防御性兜底：容量校验和 CommitHaul 之间理论上不该出现竞态（单线程、同一帧内完成），
                // 万一未来出现，CommitHaul 已经把货放回地面——这里同步清空货舱，不留双份。
                machine.Cargo = Array.Empty<CargoEntry>();
                order.State = WorkOrderState.Waiting;
                order.FailureReason = delivered.FailureReason;
                return;
            }

            machine.Cargo = Array.Empty<CargoEntry>();
            order.State = WorkOrderState.Completed;
            order.FailureReason = null;
            MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId); // ER4-MCH-01：统计与经历唯一写入口。
            MarkAssignmentDirty();
        }

        // ── Recharge ─────────────────────────────────────────────────────────────

        public static WorkOrderOpResult TryCreateRecharge(CampaignState state, int machineLogicId)
        {
            string targetId = "recharge:" + machineLogicId;
            WorkOrderRecord existing = FindActiveByTarget(state, WorkOrderKind.Recharge, targetId);
            if (existing != null)
            {
                if (existing.State != WorkOrderState.Ready)
                {
                    return WorkOrderOpResult.Fail($"order-already-active:{existing.State}");
                }
                return ReassignExisting(state, existing, machineLogicId);
            }

            MachineCheck check = CheckMachine(machineLogicId, WorkOrderKind.Recharge);
            if (!check.Ok)
            {
                return WorkOrderOpResult.Fail(check.FailureReason);
            }

            float max = HomeValleyLayout.BatteryCapacity.TryGetValue(check.Record.ChassisId, out float cap) ? cap : 100f;
            if (check.Record.Battery >= max)
            {
                return WorkOrderOpResult.Fail("battery-already-full");
            }

            string workOrderId = targetId + ":" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var order = NewOrder(state, workOrderId, WorkOrderKind.Recharge, targetId, machineLogicId,
                resourceTransactionId: null, duration: 0f);
            Append(state, order);
            return WorkOrderOpResult.Ok(workOrderId);
        }

        // ── 通用：到达工作地点（Repair/Build/Salvage/Recharge 共用）────────────────────

        public static void OnArrivedAtWork(CampaignState state, string workOrderId)
        {
            WorkOrderRecord order = Find(state, workOrderId);
            if (order == null)
            {
                return;
            }
            if (order.Kind == WorkOrderKind.Haul)
            {
                // 通用到达入口也接搬运单（自检与瞬时到达的调用方统一走这里）：取货那一腿到了 → 拾取，下一次 Tick 发起送货那一腿；
                // 送货那一腿到了 → 交付。正式的家园控制器仍按两腿各自的回调走（HaulSourceArrival / HaulDestinationArrival），行为相同。
                if (order.State == WorkOrderState.Reserved)
                {
                    if (OnArrivedAtHaulSource(state, workOrderId))
                    {
                        QueueNextLeg(order);
                    }
                }
                else if (order.State == WorkOrderState.InProgress)
                {
                    OnArrivedAtHaulDestination(state, workOrderId);
                }
                return;
            }
            if (order.State != WorkOrderState.Reserved)
            {
                return;
            }
            if (order.Kind == WorkOrderKind.Build && ArriveBuildLeg(state, order))
            {
                return;
            }

            if (!string.IsNullOrEmpty(order.ResourceTransactionId))
            {
                CampaignEconomyLedger.MarkRunning(state, order.ResourceTransactionId);
            }
            PathWatch.Remove(order.WorkOrderId); // 离开 Reserved：赶路看门狗不再需要跟踪这条订单。
            order.State = WorkOrderState.InProgress;
            order.UnreachableNotified = false; // 到了工作地点：以后再次无法到达要重新通知。
            // 故意不把 Progress 清零：新订单本来就是 0（NewOrder 初始化），但机器受控中断后被重新指派
            // 续工的订单，Progress 是"已经干了多少"的真实进度（ERD-WRK-003"机器受控...保留已搬货物"
            // 同一条纪律延伸到工作进度本身，不是只保货物），这里清零会让续工的人白干一次已完成的部分。
        }

        /// <summary>
        /// FG3-LOG-02：施工单的到达。返回 true = 本方法已处理完（取料那一腿）。
        /// - 取料腿（Leg=1，机器在仓库）：取这一趟的材料进货舱，转为去现场那一腿，下一次 Tick 发起移动；仓库里没有货 → 等待材料、放掉机器（不扣料）。
        /// - 现场腿（Leg=0）：货舱里的材料放进虚影，然后由调用方转为施工中（与其它工单同一段）。
        /// </summary>
        private static bool ArriveBuildLeg(CampaignState state, WorkOrderRecord order)
        {
            MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord machine);
            if (order.Leg == 1)
            {
                PathWatch.Remove(order.WorkOrderId);
                int got = HomeValleyConstruction.TryFetch(state, order, machine);
                if (got <= 0)
                {
                    int need = HomeValleyConstruction.MaterialsStillNeeded(state, order);
                    if (need <= 0)
                    {
                        order.Leg = 0; // 已经不缺料（材料在别处被补齐 / 规划缩短）：直接去现场。
                        QueueNextLeg(order);
                        return true;
                    }
                    EnterWaitingMaterials(state, order);
                    return true;
                }
                order.Leg = 0; // “无法到达”通知的防刷屏标记只在真正到了现场时清（取料不算），否则去不了现场时每次重试都会再发一条。
                QueueNextLeg(order);
                return true;
            }
            HomeValleyConstruction.Deposit(state, order, machine);
            return false;
        }

        /// <summary>施工单转为“等待材料”：放掉机器（机器空闲，可以去做别的），原因写明还差多少；库存有货后回到待分配池。</summary>
        private static void EnterWaitingMaterials(CampaignState state, WorkOrderRecord order)
        {
            PathWatch.Remove(order.WorkOrderId);
            ReleaseBuildMachine(state, order, dropOnly: false);
            int need = HomeValleyConstruction.MaterialsStillNeeded(state, order);
            // 每张施工单只在第一次缺料时发通知（之后“有一点货 → 取完 → 又缺”的来回不刷屏；状态一直写在队列与悬停里）。
            bool first = WaitingNotified.Add(order.WorkOrderId);
            order.State = WorkOrderState.Waiting;
            order.Leg = 1;
            order.FailureReason = HomeValleyConstruction.MaterialsReasonPrefix + need.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                  + ":" + Mathf.FloorToInt(state.Scrap).ToString(System.Globalization.CultureInfo.InvariantCulture);
            order.AssignedMachineLogicId = 0;
            MarkAssignmentDirty();
            if (first)
            {
                HomeValleyConstruction.NotifyWaitingMaterials(state, order, need);
            }
        }

        /// <summary>已经发过“等待材料”通知的施工单（瞬态；读档后同一张单最多再提醒一次）。</summary>
        private static readonly HashSet<string> WaitingNotified = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>取料 / 取货之后要发起的下一腿（下一次 <see cref="Tick"/> 开头统一调 beginAssignedMovement；上限 = 同一步到达的机器数）。</summary>
        private static readonly List<string> PendingLeg = new List<string>(4);

        private static void QueueNextLeg(WorkOrderRecord order)
        {
            if (!PendingLeg.Contains(order.WorkOrderId))
            {
                PendingLeg.Add(order.WorkOrderId);
            }
        }

        private static void DrainPendingLegs(CampaignState state, Action<WorkOrderRecord> beginAssignedMovement)
        {
            if (PendingLeg.Count == 0)
            {
                return;
            }
            var ids = PendingLeg.ToArray();
            PendingLeg.Clear();
            foreach (string id in ids)
            {
                WorkOrderRecord o = Find(state, id);
                if (o == null || o.AssignedMachineLogicId <= 0)
                {
                    continue;
                }
                bool buildLeg = o.Kind == WorkOrderKind.Build && o.State == WorkOrderState.Reserved;
                bool haulLeg = o.Kind == WorkOrderKind.Haul && o.State == WorkOrderState.InProgress;
                if (buildLeg || haulLeg)
                {
                    beginAssignedMovement?.Invoke(o);
                }
            }
        }

        /// <summary>自检用：等着发起下一腿的工单数。</summary>
        public static int PendingLegCount => PendingLeg.Count;

        // ── 通用创建辅助 ─────────────────────────────────────────────────────────

        private static WorkOrderRecord NewOrder(CampaignState state, string workOrderId, WorkOrderKind kind,
            string targetId, int machineLogicId, string resourceTransactionId, float duration)
        {
            return new WorkOrderRecord
            {
                WorkOrderId = workOrderId,
                Kind = kind,
                IssuerId = "player",
                TargetId = targetId,
                SourceId = null,
                DestinationId = null,
                RequiredTags = RequiredTagsFor(kind),
                ResourceTransactionId = resourceTransactionId,
                Priority = 0,
                CreatedTick = NowTick(state),
                AssignedMachineLogicId = machineLogicId,
                State = WorkOrderState.Reserved,
                FailureReason = null,
                RetryCount = 0,
                Progress = 0f,
                Duration = duration,
            };
        }

        private static WorkOrderOpResult ReassignExisting(CampaignState state, WorkOrderRecord order, int machineLogicId)
        {
            MachineCheck check = CheckMachine(machineLogicId, order.Kind);
            if (!check.Ok)
            {
                return WorkOrderOpResult.Fail(check.FailureReason);
            }

            // Haul 的货物挂在"上一台"机器的 Cargo 上（若已拾取），换机重派要把货物转移过去——
            // 抽象为"另一台机器接手运货"，不是复制/凭空生成。
            if (order.Kind == WorkOrderKind.Haul && MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord prev)
                && prev.Cargo != null && prev.Cargo.Length > 0)
            {
                MachineRegistry.TryGetRecord(machineLogicId, out MachineRecord next);
                if (next != null)
                {
                    next.Cargo = prev.Cargo;
                }
                prev.Cargo = Array.Empty<CargoEntry>();
            }

            order.AssignedMachineLogicId = machineLogicId;
            order.State = WorkOrderState.Reserved;
            order.RetryCount++;
            return WorkOrderOpResult.Ok(order.WorkOrderId);
        }

        // ── 中断：玩家取消 ───────────────────────────────────────────────────────

        public static void CancelOrder(CampaignState state, string workOrderId, Vector2 machineCurrentPosition)
        {
            WorkOrderRecord order = Find(state, workOrderId);
            if (order == null || IsTerminal(order.State))
            {
                return;
            }

            PathWatch.Remove(order.WorkOrderId);
            WaitingWatch.Remove(order.WorkOrderId);
            ReleaseResourcesAndCargo(state, order, machineCurrentPosition, refund: true);
            order.State = WorkOrderState.Cancelled;
            order.AssignedMachineLogicId = 0;
            MarkAssignmentDirty();
            HomeValleyConstruction.Touch();
        }

        // ── 中断：机器受控（WASD 接管）────────────────────────────────────────────

        /// <summary>ERD-WRK-003 第三条："机器受控：保留已搬货物，订单回 Ready，不复制物品"——资源
        /// 事务/货舱原样保留（不退款、不落地），只是暂停并解除机器绑定，等待玩家之后重新指派
        /// （可以是同一台机器，也可以是另一台——本 Story 不强制回到原机器，Ready 是"任何合格机器
        /// 都能接手"的通用等待态，与 ER3-WRK-02 的自动分配池是同一批候选）。</summary>
        public static void OnMachinePossessed(CampaignState state, int machineLogicId)
        {
            WorkOrderRecord order = FindActiveOrderForMachine(state, machineLogicId);
            if (order == null)
            {
                return;
            }
            PathWatch.Remove(order.WorkOrderId);
            // FG3-LOG-02：施工单被接入打断时，货舱里这一趟的材料退回仓库（机器不带着施工材料去打仗；订单回池后别的机器重新取料）。
            ReleaseBuildMachine(state, order, dropOnly: false);
            if (order.Kind == WorkOrderKind.Build)
            {
                order.Leg = HomeValleyConstruction.MaterialsStillNeeded(state, order) > 0 && order.State != WorkOrderState.InProgress ? 1 : order.Leg;
            }
            order.State = WorkOrderState.Ready;
            order.AssignedMachineLogicId = 0;
            MarkAssignmentDirty(); // 订单立即回到分配池，另一台空闲机器不必等满 0.5 秒窗口。
        }

        /// <summary>FG3-LOG-02（FGR-BASE-020 / FG-GAP-063）：玩家把机器调去做别的（右键另一个工作目标、派去远征）时，
        /// 让出它手上的工单——与接入打断同一套处理（施工单：货舱材料退回仓库、回待分配池，虚影与已到现场的材料保留）。</summary>
        public static void YieldToPool(CampaignState state, int machineLogicId) => OnMachinePossessed(state, machineLogicId);

        /// <summary>
        /// FG3-LOG-02（FGR-LOG-006 / FGR-BASE-020）：玩家选中机器、右键点中还没建成的虚影 = “去建这个”。
        /// 不新建施工单、不撤销规划、不扣料：把这座虚影现有的施工单交给这台机器（按当前这一腿走：缺料先去仓库取）。
        /// 施工单已经在别的机器手上 → 拒绝（与别的工单“已有机器在做”一致）；缺料且仓库没货 → 拒绝并写明缺什么（不白跑一趟）。
        /// </summary>
        public static WorkOrderOpResult TryAssignConstruction(CampaignState state, string siteId, int machineLogicId)
        {
            WorkOrderRecord order = state == null ? null : FindActiveBuild(state, siteId);
            if (order == null)
            {
                return WorkOrderOpResult.Fail("building-not-found");
            }
            if (order.AssignedMachineLogicId == machineLogicId)
            {
                return WorkOrderOpResult.Ok(order.WorkOrderId);
            }
            if (order.AssignedMachineLogicId > 0)
            {
                return WorkOrderOpResult.Fail($"order-already-active:{order.State}");
            }
            int need = HomeValleyConstruction.MaterialsStillNeeded(state, order);
            int stock = Mathf.FloorToInt(state.Scrap);
            if (need > 0 && stock <= 0)
            {
                return WorkOrderOpResult.Fail(HomeValleyConstruction.MaterialsReasonPrefix + need.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                              + ":" + stock.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            PathWatch.Remove(order.WorkOrderId);
            WaitingWatch.Remove(order.WorkOrderId);
            order.Leg = need > 0 ? 1 : 0;
            order.FailureReason = null;
            WorkOrderOpResult r = ReassignExisting(state, order, machineLogicId);
            if (r.Success)
            {
                MarkAssignmentDirty();
                HomeValleyConstruction.Touch();
            }
            return r;
        }

        // ── 每帧驱动 ─────────────────────────────────────────────────────────────

        /// <summary>由 <see cref="HomeValleyController.Update"/> 每帧调用一次，只遍历当前非终态订单
        /// （归还谷地量级恒定个位数，不违反"热更层每帧不得 O(建筑/敌人数)"的性能纪律——那条规则约束
        /// 的是战斗热路径，不是这种个位数量级的战略层管理循环）。
        /// <paramref name="getMachinePosition"/>：查询机器当前世界 XZ 坐标（用于赶路阶段的路径停滞
        /// 看门狗，以及 ER3-WRK-02 分配引擎估算路径距离），找不到返回 null。<paramref name="releaseMachineMovement"/>：
        /// PathBlocked 触发时让调用方对应地 <c>marker.CancelCommandMove()</c>。<paramref name="isDirectControlled"/>：
        /// ERD-WRK-002"直控机器暂不领取新单"的判定来源（<see cref="HomeValleyController"/> 的 <c>_possessed</c>
        /// 是私有字段，只能靠委托查询，不下沉到本类）。<paramref name="beginAssignedMovement"/>：分配引擎选中
        /// 一台空闲机器后，让调用方对该订单发起真实移动（复用玩家点选下令同一条移动链）。本类不直接依赖
        /// MonoBehaviour/Transform 类型。</summary>
        /// <param name="remainingPath">FG0-ARCH-06：机器沿路线剩余的长度（米；不在沿路线走返回 null）。赶路看门狗用它判断“有没有在推进”——
        /// 绕路时直线距离会暂时变大，只看直线会把正常绕行误判成路径受阻。</param>
        /// <param name="resumeDelivery">FG0-ARCH-06：搬运送货那一腿（核心）无法到达、等待 30 秒后，重新下达送货赶路。</param>
        public static void Tick(CampaignState state, float dt, Func<int, Vector2?> getMachinePosition,
            Action<int> releaseMachineMovement, Func<int, bool> isDirectControlled, Action<WorkOrderRecord> beginAssignedMovement,
            Func<int, float?> remainingPath = null, Action<WorkOrderRecord> resumeDelivery = null)
        {
            if (state == null)
            {
                return;
            }
            DrainPendingMovementRelease(state, releaseMachineMovement);
            DrainPendingLegs(state, beginAssignedMovement);
            AllocateFetchStock(state, dt);

            if (state.WorkOrders != null && state.WorkOrders.Length > 0)
            {
                foreach (WorkOrderRecord order in state.WorkOrders)
                {
                    if (IsTerminal(order.State))
                    {
                        continue;
                    }
                    if (order.Kind == WorkOrderKind.Build && order.ResourceTransactionId != null)
                    {
                        HomeValleyConstruction.MigrateLegacyBuildOrder(state, order); // FG3-LOG-02：旧存档“放置即预留”的施工单，只迁移一次。
                    }
                    if (order.State == WorkOrderState.Ready && order.AssignedMachineLogicId == 0)
                    {
                        // FG3-LOG-02：取料腿的施工单在库存为 0 时不派机器（派过去也是空跑），转为等待材料；
                        // 返还物的搬运单在仓库放不下时同样等待，不让机器扛着货在核心门口干等。
                        if (order.Kind == WorkOrderKind.Build && order.Leg == 1 && Mathf.FloorToInt(state.Scrap) <= 0
                            && HomeValleyConstruction.MaterialsStillNeeded(state, order) > 0)
                        {
                            EnterWaitingMaterials(state, order);
                            continue;
                        }
                        if (order.Kind == WorkOrderKind.Haul && order.IssuerId == "return" && !ReturnHaulHasSpace(state, order))
                        {
                            order.State = WorkOrderState.Waiting;
                            order.FailureReason = HomeValleyConstruction.ReturnWaitReason;
                            continue;
                        }
                    }

                    // 机器死亡检查：对当前仍绑定机器的订单统一检查存活状态，覆盖 Reserved/InProgress 两态。
                    if (order.AssignedMachineLogicId > 0
                        && (!MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord assignedRecord) || !assignedRecord.IsAlive))
                    {
                        HandleMachineDeath(state, order);
                        continue;
                    }

                    switch (order.State)
                    {
                        case WorkOrderState.Reserved:
                            TickReserved(state, order, dt, getMachinePosition, releaseMachineMovement, remainingPath);
                            break;
                        case WorkOrderState.InProgress:
                            TickInProgress(state, order, dt, resumeDelivery);
                            break;
                        case WorkOrderState.Waiting:
                            TickWaiting(state, order, dt);
                            break;
                    }
                }
            }

            TickBatteryRegen(state, dt);
            TickAssignment(state, dt, getMachinePosition, isDirectControlled, beginAssignedMovement);
            HomeValleyConstruction.TickLabor(state, dt, isDirectControlled);
        }

        private static bool IsWaitingMaterials(WorkOrderRecord order) =>
            order.Kind == WorkOrderKind.Build && order.State == WorkOrderState.Waiting && order.FailureReason != null
            && order.FailureReason.StartsWith(HomeValleyConstruction.MaterialsReasonPrefix, StringComparison.Ordinal);

        /// <summary>取料库存分配的脏标记与节流计时（瞬态；读档后第一次 Tick 必跑一次）。</summary>
        private static bool _fetchDirty = true;
        private static float _fetchTimer;
        private static int _fetchLastStock = int.MinValue;
        private static readonly List<WorkOrderRecord> FetchCandidates = new List<WorkOrderRecord>(16);

        /// <summary>
        /// FG3-LOG-02（审查修复：库存不够分时多台机器空跑、优先级失效）：把仓库里的材料按“施工优先级高 → 放下得早”分给要取料的施工单。
        /// 预算 = 库存 − 正在去仓库取料的施工单这一趟要拿的量；排在前面、预算够的施工单留在 / 回到待分配池，
        /// 排不上的转为“等待材料”（放掉机器、写明缺什么，每张单只通知一次），预算腾出来后按同一顺序放回。
        /// 已经不缺料的等待单直接回池去现场。只在库存变化、工单状态变化（<see cref="MarkAssignmentDirty"/>）或每 0.5 秒时跑，
        /// O(工单数)；候选按优先级做稳定插入排序（候选 = 等料 / 待取料的虚影数）。
        /// </summary>
        private static void AllocateFetchStock(CampaignState state, float dt)
        {
            WorkOrderRecord[] orders = state.WorkOrders;
            if (orders == null || orders.Length == 0)
            {
                return;
            }
            int stock = Mathf.FloorToInt(state.Scrap);
            _fetchTimer += dt;
            if (!_fetchDirty && stock == _fetchLastStock && _fetchTimer < AssignIntervalSeconds)
            {
                return;
            }
            _fetchDirty = false;
            _fetchTimer = 0f;
            _fetchLastStock = stock;

            FetchCandidates.Clear();
            int committed = 0;
            for (int i = 0; i < orders.Length; i++)
            {
                WorkOrderRecord o = orders[i];
                if (o == null || o.Kind != WorkOrderKind.Build || IsTerminal(o.State))
                {
                    continue;
                }
                if (IsWaitingMaterials(o))
                {
                    FetchCandidates.Add(o);
                    continue;
                }
                if (o.Leg != 1)
                {
                    continue;
                }
                if (o.State == WorkOrderState.Reserved && o.AssignedMachineLogicId > 0)
                {
                    committed += Math.Min(HomeValleyConstruction.CarryPerTrip, HomeValleyConstruction.MaterialsStillNeeded(state, o));
                }
                else if (o.State == WorkOrderState.Ready && o.AssignedMachineLogicId == 0)
                {
                    FetchCandidates.Add(o);
                }
            }
            if (FetchCandidates.Count == 0)
            {
                return;
            }
            // 稳定插入排序：优先级高的在前，同优先级保持工单数组顺序（= 放下先后，与施工队列同一顺序）。
            for (int i = 1; i < FetchCandidates.Count; i++)
            {
                WorkOrderRecord cur = FetchCandidates[i];
                int j = i - 1;
                while (j >= 0 && FetchCandidates[j].Priority < cur.Priority)
                {
                    FetchCandidates[j + 1] = FetchCandidates[j];
                    j--;
                }
                FetchCandidates[j + 1] = cur;
            }

            int budget = stock - committed;
            bool changed = false;
            foreach (WorkOrderRecord o in FetchCandidates)
            {
                int need = HomeValleyConstruction.MaterialsStillNeeded(state, o);
                bool waiting = o.State == WorkOrderState.Waiting;
                if (need <= 0)
                {
                    if (waiting || o.Leg != 0)
                    {
                        o.Leg = 0; // 已经不缺料（材料在别处被补齐 / 规划缩短）：直接去现场。
                        o.State = WorkOrderState.Ready;
                        o.FailureReason = null;
                        changed = true;
                    }
                    continue;
                }
                if (budget > 0)
                {
                    budget -= Math.Min(HomeValleyConstruction.CarryPerTrip, need);
                    if (waiting)
                    {
                        o.Leg = 1;
                        o.State = WorkOrderState.Ready;
                        o.FailureReason = null;
                        changed = true;
                    }
                    continue;
                }
                if (!waiting)
                {
                    EnterWaitingMaterials(state, o); // 排不上：等待材料（不派机器空跑）。
                    changed = true;
                }
            }
            FetchCandidates.Clear();
            if (changed)
            {
                _assignDirty = true;
                HomeValleyConstruction.Touch();
            }
        }

        /// <summary>返还物搬运单：地面物还在、仓库放得下它（废料以外的物品家园仓库存不了，一直等）。</summary>
        private static bool ReturnHaulHasSpace(CampaignState state, WorkOrderRecord order)
        {
            GroundItemRecord item = HomeValleyCargo.FindGroundItem(state, order.SourceId);
            return item != null && item.ResourceType == CampaignEconomyLedger.ResourceScrap
                   && HomeValleyCargo.GetAvailableSpace(state, item.ResourceType) >= item.Amount;
        }

        // ── ER3-WRK-02：确定性自动分配 ───────────────────────────────────────────

        /// <summary>脏事件标记——不落盘（进程重启后最坏情况是多等一次 0.5 秒窗口，不是正确性问题，
        /// 同 <see cref="PathWatch"/> 一类瞬态记忆的既定处理方式）。订单完成/取消/死亡/回 Ready 时
        /// 由本类各终止路径自行标记；玩家改动机器工作偏好由调用方（UI）在写入
        /// <see cref="MachineRegistry.TrySetWorkPriority"/> 成功后调用本方法。</summary>
        private static bool _assignDirty = true;
        private static readonly List<Vector2> ReadyPositions = new List<Vector2>(16);
        private static float _assignTimer;
        private const float AssignIntervalSeconds = 0.5f;

        public static void MarkAssignmentDirty()
        {
            _assignDirty = true;
            _fetchDirty = true;
        }

        // ── FG0-ARCH-06：工作地点无法到达 ─────────────────────────────────────────────

        /// <summary>“无法到达”的失败原因码：unreachable:<NavFailReason 数值>。</summary>
        public const string UnreachablePrefix = "unreachable:";

        public static bool IsUnreachableReason(string reason) => reason != null && reason.StartsWith(UnreachablePrefix, StringComparison.Ordinal);

        public static BinGames.Sim.Nav.NavFailReason ParseUnreachable(string reason)
        {
            if (IsUnreachableReason(reason) && int.TryParse(reason.Substring(UnreachablePrefix.Length), out int v))
            {
                return (BinGames.Sim.Nav.NavFailReason)v;
            }
            return BinGames.Sim.Nav.NavFailReason.Unreachable;
        }

        /// <summary>
        /// 内核报告机器的工作赶路寻路失败（FG0-ARCH-06：“寻路失败时给出明确原因，不让单位原地发呆”）：
        /// 赶路阶段的工单转为等待（原因 unreachable:*，30 秒后与“路径受阻”同一套重试），机器立即空出来；搬运送货那一腿保持在办、30 秒后重发送货赶路。
        /// 每张工单第一次无法到达时发一条可定位的“无法到达”通知（带原因与解决办法），之后的重试不再刷屏。
        /// </summary>
        public static void OnWorkUnreachable(CampaignState state, int logicId, BinGames.Sim.Nav.NavFailReason reason, Vector2 at)
        {
            if (state == null)
            {
                return;
            }
            WorkOrderRecord order = FindActiveOrderForMachine(state, logicId);
            if (order == null)
            {
                // 没有工单的直接移动命令（右键地面）：命令已结束，也要告诉玩家为什么没动（<paramref name="at"/> = 目标点）。
                string text = GameLogic.Localization.GameText.Format("nav.squad.unreachable", logicId.ToString(),
                    Nav.NavService.FailText(reason, Nav.NavService.CellOf(at.x, at.y)));
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, text);
                GameLogic.Notifications.NotificationCenter.Post("unreachable", text, new Vector3(at.x, 0f, at.y));
                return;
            }
            string code = UnreachablePrefix + ((int)reason).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Vector2 target = ResolveWorkPosition(state, order);
            if (order.State == WorkOrderState.Reserved)
            {
                PathWatch.Remove(order.WorkOrderId);
                ReleaseBuildMachine(state, order, dropOnly: false); // FG3-LOG-02：去不了现场，货舱里的材料退回仓库。
                if (order.Kind == WorkOrderKind.Build && HomeValleyConstruction.MaterialsStillNeeded(state, order) > 0)
                {
                    order.Leg = 1;
                }
                order.State = WorkOrderState.Waiting;
                order.FailureReason = code;
                order.AssignedMachineLogicId = 0;
                WaitingWatch[order.WorkOrderId] = 0f;
            }
            else if (order.State == WorkOrderState.InProgress && order.Kind == WorkOrderKind.Haul)
            {
                order.FailureReason = code;
                WaitingWatch[order.WorkOrderId] = 0f;
            }
            else
            {
                return;
            }
            if (!order.UnreachableNotified)
            {
                order.UnreachableNotified = true;
                string reasonText = Nav.NavService.FailText(reason, Nav.NavService.CellOf(target.x, target.y));
                string detail = GameLogic.Localization.GameText.Format("nav.work.unreachable", Feedback.FeedbackCues.MachineLabel(logicId),
                    DescribeTarget(state, order), reasonText, GameLogic.Localization.GameText.Get("nav.fail.fix"));
                GameLogic.Notifications.NotificationCenter.Post("unreachable", detail, new Vector3(target.x, 0f, target.y));
            }
            UnityEngine.Debug.Log($"[HomeValleyWorkOrders] 工单 {order.WorkOrderId} 无法到达（{reason}），转为等待，30 秒后重试。");
        }

        /// <summary>工单目标的显示名（建筑名；其它按目标 ID）。</summary>
        public static string DescribeTarget(CampaignState state, WorkOrderRecord order)
        {
            if (HomeValleyConstruction.IsBeltPlan(order.TargetId))
            {
                PlannedBeltRecord plan = HomeValleyConstruction.FindPlan(state, order.TargetId);
                int built = HomeValleyConstruction.BuiltCells(plan);
                int total = built + HomeValleyConstruction.UnbuiltCells(plan);
                return GameLogic.Localization.GameText.Format("build.queue.belt_name",
                    HomeValleyConstruction.PieceName(plan), total.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    built.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            BuildingRecord b = state?.BuildingRecords?.FirstOrDefault(x => x.BuildingId == order.TargetId);
            if (b != null)
            {
                // FG3-LOG-01：搬迁目标虚影的施工单写明是搬迁（“维修台（搬迁）”），不与新建混淆。FG3-LOG-07：升级写“电塔 T1 → 电塔 T2”。
                if (Grid.HomeGridService.IsUpgradeGhost(b))
                {
                    BuildingRecord from = state.BuildingRecords.FirstOrDefault(x => x.BuildingId == b.RelocateFromId);
                    return GameLogic.Localization.GameText.Format("plan.upgrade.order", Grid.HomeGridService.DisplayName(from?.BuildingTypeId ?? b.BuildingTypeId),
                        Grid.HomeGridService.DisplayName(b.BuildingTypeId));
                }
                return !string.IsNullOrEmpty(b.RelocateFromId)
                    ? GameLogic.Localization.GameText.Format("ui.build.relocate_order", Grid.HomeGridService.DisplayName(b.BuildingTypeId))
                    : Grid.HomeGridService.DisplayName(b.BuildingTypeId);
            }
            if (order.Kind == WorkOrderKind.Recharge || order.Kind == WorkOrderKind.Haul)
            {
                return Grid.HomeGridService.DisplayName(HomeValleyLayout.BuildingTypeCore);
            }
            return order.TargetId ?? string.Empty;
        }

        /// <summary>FG0-ARCH-01：接到一个战役（新建 / 读档 / 回滚）时清空本类的瞬态记忆（分配计时、赶路与等待看门狗）。
        /// 不清的话，上一次载入残留的计时会让同一存档读两次跑出不同结果（观察 / 不观察对照自检会发现）。</summary>
        public static void ResetSessionState()
        {
            _assignDirty = true;
            _assignTimer = 0f;
            _fetchDirty = true;
            _fetchTimer = 0f;
            _fetchLastStock = int.MinValue;
            FetchCandidates.Clear();
            PathWatch.Clear();
            WaitingWatch.Clear();
            PendingMovementRelease.Clear();
            PendingLeg.Clear();
            WaitingNotified.Clear();
            HomeValleyConstruction.ResetSessionState();
            Grid.PlanHistory.ResetSession(); // FG3-LOG-07：撤销栈在存档里；这里只清“正在记录的一步”这类瞬态
        }

        /// <summary>ERD-WRK-002："空闲机器每 0.5 秒或收到脏事件时评估一次"——本方法就是那次评估，
        /// 不在每帧都跑，满足 AC-PER-003/006 对 64 机/200 单场景"无热更每帧全量扫描"的要求。</summary>
        private static void TickAssignment(CampaignState state, float dt, Func<int, Vector2?> getMachinePosition,
            Func<int, bool> isDirectControlled, Action<WorkOrderRecord> beginAssignedMovement)
        {
            _assignTimer += dt;
            if (!_assignDirty && _assignTimer < AssignIntervalSeconds)
            {
                return;
            }
            _assignTimer = 0f;
            _assignDirty = false;

            if (beginAssignedMovement == null || state.WorkOrders == null || state.WorkOrders.Length == 0)
            {
                return;
            }

            List<WorkOrderRecord> readyOrders = null;
            foreach (WorkOrderRecord order in state.WorkOrders)
            {
                if (order.State == WorkOrderState.Ready)
                {
                    (readyOrders ??= new List<WorkOrderRecord>()).Add(order);
                }
            }
            if (readyOrders == null)
            {
                return;
            }

            // 候选机器：存活、在归还谷地区域内、不在厂内、不在直控、当前没有在办订单——
            // 按 LogicId 升序排序，不依赖 MachineRegistry.AllRecords（Dictionary.Values）的枚举顺序。
            List<MachineRecord> idleMachines = null;
            foreach (MachineRecord machine in MachineRegistry.AllRecords)
            {
                if (machine.RegionId != HomeValleyLayout.RegionId || !machine.IsAlive || machine.IsInFactory)
                {
                    continue;
                }
                if (isDirectControlled != null && isDirectControlled(machine.LogicId))
                {
                    continue;
                }
                if (FindActiveOrderForMachine(state, machine.LogicId) != null)
                {
                    continue;
                }
                // FG1-SIG-07（FGR-SIG-053）：覆盖外的机器收不到家园派工（远程命令）；回到覆盖后覆盖服务标脏，下一轮自动恢复。读内核标志，O(1)。
                if (Signal.SignalCoverageService.IsMachineOutOfCoverage(machine.LogicId))
                {
                    continue;
                }
                (idleMachines ??= new List<MachineRecord>()).Add(machine);
            }
            if (idleMachines == null)
            {
                return;
            }
            idleMachines.Sort((a, b) => a.LogicId.CompareTo(b.LogicId));

            // FG3-LOG-09：工作地点与机器无关，每张待派工单每轮只解析一次（改前每台空闲机器 × 每张工单各解析一次）。
            ReadyPositions.Clear();
            foreach (WorkOrderRecord order in readyOrders)
            {
                ReadyPositions.Add(ResolveWorkPosition(state, order));
            }

            foreach (MachineRecord machine in idleMachines)
            {
                WorkOrderRecord best = null;
                int bestCategoryPriority = 0;
                float bestDistance = 0f;
                Vector2 fromPos = getMachinePosition?.Invoke(machine.LogicId) ?? machine.WorldPosition;

                for (int oi = 0; oi < readyOrders.Count; oi++)
                {
                    WorkOrderRecord order = readyOrders[oi];
                    if (order.AssignedMachineLogicId != 0)
                    {
                        continue; // 本轮已被排在前面（LogicId 更小）的机器领走。
                    }
                    if (!IsCapable(machine.ChassisId, order.Kind))
                    {
                        continue;
                    }
                    if (order.Kind == WorkOrderKind.Haul && order.IssuerId == "return" && !ReturnHaulHasSpace(state, order))
                    {
                        continue; // FG3-LOG-02：返还物搬运单在仓库放不下时不派机器（本步新生成、还没被转为等待的也在这里拦住）。
                    }
                    int categoryPriority = machine.WorkPriorities?.Get(order.Kind) ?? 0;
                    if (categoryPriority <= 0)
                    {
                        continue; // 0＝该机器对这一类工作永久禁用（不影响玩家直接点选下令）。
                    }

                    float distance = Vector2.Distance(fromPos, ReadyPositions[oi]);
                    if (best == null || IsBetterCandidate(categoryPriority, order, distance, bestCategoryPriority, best, bestDistance))
                    {
                        best = order;
                        bestCategoryPriority = categoryPriority;
                        bestDistance = distance;
                    }
                }

                if (best == null)
                {
                    continue;
                }

                WorkOrderOpResult assigned = ReassignExisting(state, best, machine.LogicId);
                if (assigned.Success)
                {
                    beginAssignedMovement(best);
                }
            }
        }

        /// <summary>ERD-WRK-002 排序键，从高到低：机器对该类工作的优先级(1～4) → 订单自身 Priority →
        /// createdTick 小者优先 → 估算路径短者优先 → 在工单数组里靠前者优先（最终稳定平局判定：调用方按数组顺序遍历，
        /// 完全平局时返回 false 保留先遇到的那张）。FG3-LOG-02：此前最后按 workOrderId 字典序，而施工单 ID 带随机段，
        /// 同一时刻放下的虚影谁先建每局不同、也和施工队列（同优先级按数组顺序）对不上。数组顺序随存档落盘，
        /// 全部输入都是落盘字段或本帧计算的确定值，不含墙上时间或随机数，保证 AC-WRK-001"同一订单集分配一致"。</summary>
        private static bool IsBetterCandidate(int candidateCategoryPriority, WorkOrderRecord candidate, float candidateDistance,
            int bestCategoryPriority, WorkOrderRecord best, float bestDistance)
        {
            if (candidateCategoryPriority != bestCategoryPriority)
            {
                return candidateCategoryPriority > bestCategoryPriority;
            }
            if (candidate.Priority != best.Priority)
            {
                return candidate.Priority > best.Priority;
            }
            if (candidate.CreatedTick != best.CreatedTick)
            {
                return candidate.CreatedTick < best.CreatedTick;
            }
            if (!Mathf.Approximately(candidateDistance, bestDistance))
            {
                return candidateDistance < bestDistance;
            }
            return false;
        }

        /// <summary>赶路阶段停滞看门狗的瞬态记忆——不落盘，不需要落盘：真实进程重启后最多重新起算
        /// 一次 5 秒窗口，不是正确性问题，只是"停滞计时器归零"，见类注释"死亡/受控没有真实触发源"
        /// 同一段讨论。按 WorkOrderId 建索引；订单离开 Reserved（到达/取消/死亡/PathBlocked 触发）时
        /// 由 <see cref="Tick"/> 顶部统一清理，不会无界增长（上限＝当前赶路中的机器数，个位数）。</summary>
        private static readonly Dictionary<string, (float lastDistance, float stallSeconds)> PathWatch =
            new Dictionary<string, (float, float)>(4);

        /// <summary>ERD-WRK-003 第二条看门狗：赶路阶段连续 <see cref="HomeValleyLayout.PathStallSeconds"/>
        /// 秒净位移不推进即判定 PathBlocked。停滞计时存在 <see cref="PathWatch"/>（瞬态），不占用
        /// <see cref="WorkOrderRecord.Progress"/>/<see cref="WorkOrderRecord.Duration"/>——那两个字段在
        /// Reserved 阶段已经承载真实含义（Repair/Build/Salvage 创建时就写入了完工所需的工作时长，
        /// 供稍后 InProgress 阶段直接使用），复用会把这份数据在赶路途中冲掉。</summary>
        private static void TickReserved(CampaignState state, WorkOrderRecord order, float dt,
            Func<int, Vector2?> getMachinePosition, Action<int> releaseMachineMovement, Func<int, float?> remainingPath = null)
        {
            if (getMachinePosition == null || order.AssignedMachineLogicId <= 0)
            {
                return;
            }
            Vector2? pos = getMachinePosition(order.AssignedMachineLogicId);
            if (!pos.HasValue)
            {
                return;
            }

            Vector2 targetPos = ResolveWorkPosition(state, order);
            float distance = Vector2.Distance(pos.Value, targetPos);
            // FG0-ARCH-06：沿寻路路线走时按剩余路线长度判断推进（绕路时直线距离会变大）。
            float? along = remainingPath?.Invoke(order.AssignedMachineLogicId);
            if (along.HasValue)
            {
                distance = along.Value;
            }

            if (!PathWatch.TryGetValue(order.WorkOrderId, out (float lastDistance, float stallSeconds) watch))
            {
                PathWatch[order.WorkOrderId] = (distance, 0f);
                return;
            }

            if (distance < watch.lastDistance - HomeValleyLayout.PathStallEpsilon)
            {
                PathWatch[order.WorkOrderId] = (distance, 0f);
                return;
            }

            float stallSeconds = watch.stallSeconds + dt;
            if (stallSeconds >= HomeValleyLayout.PathStallSeconds)
            {
                PathWatch.Remove(order.WorkOrderId);
                int releasedMachine = order.AssignedMachineLogicId;
                ReleaseBuildMachine(state, order, dropOnly: false); // FG3-LOG-02：运料途中受阻，这一趟的材料退回仓库，重试时重新取。
                if (order.Kind == WorkOrderKind.Build && HomeValleyConstruction.MaterialsStillNeeded(state, order) > 0)
                {
                    order.Leg = 1;
                }
                order.State = WorkOrderState.Waiting;
                order.FailureReason = "path-blocked";
                order.AssignedMachineLogicId = 0;
                // 注意：不碰 Progress/Duration——Repair/Build/Salvage 在 Reserved 阶段就已经写入了真实
                // 工作时长，PathBlocked 只是"赶路没走到"，那份数据要留到真正 InProgress 时使用，
                // 30 秒冷却单独存 WaitingWatch（瞬态，同 PathWatch）。
                WaitingWatch[order.WorkOrderId] = 0f;
                releaseMachineMovement?.Invoke(releasedMachine);
            }
            else
            {
                PathWatch[order.WorkOrderId] = (watch.lastDistance, stallSeconds);
            }
        }

        /// <summary>Waiting/path-blocked 的 30 秒冷却计时——瞬态，理由同 <see cref="PathWatch"/>。</summary>
        private static readonly Dictionary<string, float> WaitingWatch = new Dictionary<string, float>(4);

        /// <summary>目标的世界 XZ 坐标——赶路看门狗（<see cref="TickReserved"/>）与 ER3-WRK-02 分配
        /// 引擎/自动分配后触发移动的调用方（<see cref="HomeValleyController"/>）共用同一份解析，
        /// 保证"看门狗判定的距离"与"实际走过去的目的地"永远一致。</summary>
        public static Vector2 ResolveWorkPosition(CampaignState state, WorkOrderRecord order)
        {
            switch (order.Kind)
            {
                case WorkOrderKind.Repair:
                    BuildingRecord repairTarget = HomeValleyConstruction.FindBuildingFast(state, order.TargetId); // FG3-LOG-09：O(1)，派工每轮每台机器都要算
                    return repairTarget?.Position ?? Vector2.zero;
                case WorkOrderKind.Build:
                    // FG0-ARCH-04：建造位由格网决定（规划中的建筑记录的占地中心）。此前恒返回发电机2 的坑位，
                    // 信标与自由放置的建筑会让机器走错地方。
                    // FG3-LOG-02：取料腿先去离现场最近的仓库（归还核心 / 运转中的仓库）；传送带规划的现场 = 下一格还没建成的格子。
                    Vector2 site = HomeValleyConstruction.SitePosition(state, order);
                    return order.Leg == 1 ? HomeValleyConstruction.StoragePosition(state, site) : site;
                case WorkOrderKind.Salvage:
                    if (order.TargetId == HomeValleyLayout.Wreckage1NodeId)
                    {
                        return HomeValleyLayout.Wreckage1.Position;
                    }
                    if (order.TargetId == HomeValleyLayout.Wreckage2NodeId)
                    {
                        return HomeValleyLayout.Wreckage2.Position;
                    }
                    // FG0-ARCH-04：拆除建筑走到建筑本身（此前落到残骸 2 的位置）。
                    BuildingRecord demolishTarget = HomeValleyConstruction.FindBuildingFast(state, order.TargetId);
                    return demolishTarget?.Position ?? HomeValleyLayout.Wreckage2.Position;
                case WorkOrderKind.Haul:
                    GroundItemRecord item = HomeValleyCargo.FindGroundItem(state, order.SourceId);
                    return item?.Position ?? HomeValleyLayout.Core.Position;
                case WorkOrderKind.Recharge:
                    return HomeValleyLayout.Core.Position;
                default:
                    return Vector2.zero;
            }
        }

        private static void TickInProgress(CampaignState state, WorkOrderRecord order, float dt, Action<WorkOrderRecord> resumeDelivery = null)
        {
            if (order.Kind == WorkOrderKind.Haul && IsUnreachableReason(order.FailureReason))
            {
                // FG0-ARCH-06：货在货舱、核心无法到达：30 秒后重新下达送货赶路（期间原因显示在工单面板）。
                float waited = (WaitingWatch.TryGetValue(order.WorkOrderId, out float w) ? w : 0f) + dt;
                if (waited >= HomeValleyLayout.PathBlockedRetrySeconds)
                {
                    WaitingWatch.Remove(order.WorkOrderId);
                    order.FailureReason = null;
                    resumeDelivery?.Invoke(order);
                }
                else
                {
                    WaitingWatch[order.WorkOrderId] = waited;
                }
                return;
            }
            switch (order.Kind)
            {
                case WorkOrderKind.Repair:
                case WorkOrderKind.Build:
                case WorkOrderKind.Salvage:
                    TickTimedWork(state, order, dt);
                    break;
                case WorkOrderKind.Recharge:
                    TickRecharge(state, order, dt);
                    break;
                case WorkOrderKind.Haul:
                    break; // Haul 的 InProgress 是"货在货舱、正走向交付点"，由到达回调驱动，Tick 不推进。
            }
        }

        private static void TickWaiting(CampaignState state, WorkOrderRecord order, float dt)
        {
            if (order.FailureReason == "path-blocked" || IsUnreachableReason(order.FailureReason))
            {
                float waited = (WaitingWatch.TryGetValue(order.WorkOrderId, out float w) ? w : 0f) + dt;
                if (waited >= HomeValleyLayout.PathBlockedRetrySeconds)
                {
                    WaitingWatch.Remove(order.WorkOrderId);
                    order.State = WorkOrderState.Ready;
                    order.FailureReason = null;
                    MarkAssignmentDirty(); // ERD-WRK-003"30秒后重试"：立即让分配引擎重新评估这条订单。
                }
                else
                {
                    WaitingWatch[order.WorkOrderId] = waited;
                }
                return;
            }

            if (order.Kind == WorkOrderKind.Haul && order.FailureReason != null && order.FailureReason.StartsWith("storage-full"))
            {
                TryDeliverHaul(state, order); // 每帧轻量重试：仓储腾出空间即可自动完成，不需要玩家手动点collect。
                return;
            }

            // FG3-LOG-02：等待材料的施工单由 AllocateFetchStock 按“优先级 → 放下先后”在库存够用的范围内放回待分配池
            // （FGR-LOG-006“一旦有货就自动继续”；库存不够分时只放出够用的几张，不让多台机器一起空跑）。
            if (IsWaitingMaterials(order))
            {
                return;
            }

            // FG3-LOG-02：返还物搬运单等仓库腾出空间（地面物被玩家亲手搬走了就取消）。
            if (order.Kind == WorkOrderKind.Haul && order.FailureReason == HomeValleyConstruction.ReturnWaitReason)
            {
                if (HomeValleyCargo.FindGroundItem(state, order.SourceId) == null)
                {
                    order.State = WorkOrderState.Cancelled;
                    order.FailureReason = "source-vanished";
                }
                else if (ReturnHaulHasSpace(state, order))
                {
                    order.State = WorkOrderState.Ready;
                    order.FailureReason = null;
                    MarkAssignmentDirty();
                }
                return;
            }

            // "no-power" 分支不在本 Story 落地——见 TickTimedWork 上方注释，当前没有真实触发源。
        }

        /// <summary>ERD-WRK-003 第五条"断电：建筑相关订单 Waiting/NoPower；维修发电机订单不依赖被修
        /// 目标供电"：勘查确认归还谷地当前没有 Repair/Build 会真实触发这条规则的场景——Repair/Build
        /// 的目标在完工前从不是电网消费者本身（<see cref="BuildingConstructionState.Damaged"/>/
        /// <see cref="BuildingConstructionState.Planned"/> 建筑不在 <see cref="HomeValleyPowerGrid.Recompute"/>
        /// 的 Operational 消费者集合里），"发电机例外"这句话要成立的前提（发电机本身会被断电阻塞）
        /// 也就不存在。真正会出现"Operational 建筑运行中被断电暂停"的场景是工厂队列消耗中途断电——
        /// 那是 ERD-FAC-001 自己的 <see cref="FactoryQueueState.WaitingPower"/>，属于 ER4-FAC-01，
        /// 不是本 Story 的 Repair/Build。此处不发明一个当前测不出真实分支的假状态；DEBT 已登记在
        /// evidence 文档，若未来出现"Operational 建筑上的 Repair/Build 类工作单"（当前没有），
        /// 届时补齐即可，不影响本 Story 已实现规则的正确性。</summary>
        private static void TickTimedWork(CampaignState state, WorkOrderRecord order, float dt)
        {
            if (order.Kind == WorkOrderKind.Build)
            {
                TickBuildWork(state, order, dt);
                return;
            }
            order.Progress += dt;
            if (order.Progress < order.Duration)
            {
                return;
            }

            switch (order.Kind)
            {
                case WorkOrderKind.Repair:
                    CompleteRepair(state, order);
                    break;
                case WorkOrderKind.Salvage:
                    CompleteSalvage(state, order);
                    break;
            }
        }

        /// <summary>
        /// FG3-LOG-02：施工单的施工进度——不超过已到现场的材料允许的上限（<see cref="HomeValleyConstruction.TickProgress"/>）。
        /// 材料用完还没建完：同一台机器回仓库再取一趟；库存为 0 就等待材料并放掉机器（“施工暂停，显示缺料，不重复扣料”）。
        /// </summary>
        private static void TickBuildWork(CampaignState state, WorkOrderRecord order, float dt)
        {
            if (order.ResourceTransactionId != null)
            {
                HomeValleyConstruction.MigrateLegacyBuildOrder(state, order);
            }
            BuildingRecord relocation = HomeValleyConstruction.FindBuildingFast(state, order.TargetId); // FG3-LOG-09：每步每张施工单，O(1)
            // FG3-LOG-07：升级虚影要材料（新旧差额），走下面的取料 / 施工进度；搬迁不花材料，按工期施工。
            if (relocation != null && !string.IsNullOrEmpty(relocation.RelocateFromId) && relocation.ConstructionRequired <= 0)
            {
                order.Progress += dt; // 搬迁不花材料（材料就是原建筑本身），按工期施工。
                if (order.Progress >= order.Duration)
                {
                    CompleteBuild(state, order);
                }
                return;
            }
            switch (HomeValleyConstruction.TickProgress(state, order, dt))
            {
                case HomeValleyConstruction.ProgressResult.Complete:
                    CompleteBuild(state, order);
                    break;
                case HomeValleyConstruction.ProgressResult.SiteGone:
                    order.State = WorkOrderState.Failed;
                    order.FailureReason = "target-destroyed";
                    MarkAssignmentDirty();
                    break;
                case HomeValleyConstruction.ProgressResult.MoveOn:
                    // 传送带：身边的格子建完了，走到下一格接着建（同一台机器、同一张单，材料已在现场）。
                    order.Leg = 0;
                    order.State = WorkOrderState.Reserved;
                    PathWatch.Remove(order.WorkOrderId);
                    QueueNextLeg(order);
                    HomeValleyConstruction.Touch();
                    break;
                case HomeValleyConstruction.ProgressResult.NeedMaterials:
                    order.Leg = 1;
                    if (Mathf.FloorToInt(state.Scrap) > 0 && order.AssignedMachineLogicId > 0)
                    {
                        order.State = WorkOrderState.Reserved; // 同一台机器回仓库再取一趟（下一次 Tick 发起移动）。
                        QueueNextLeg(order);
                    }
                    else
                    {
                        EnterWaitingMaterials(state, order);
                    }
                    break;
            }
        }

        private static void CompleteRepair(CampaignState state, WorkOrderRecord order)
        {
            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == order.TargetId);
            if (building == null)
            {
                order.State = WorkOrderState.Failed;
                order.FailureReason = "target-destroyed";
                if (!string.IsNullOrEmpty(order.ResourceTransactionId))
                {
                    CampaignEconomyLedger.Cancel(state, order.ResourceTransactionId);
                }
                MarkAssignmentDirty();
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Failure, "修复工单失败：目标建筑已不存在，废料已退还");
                return;
            }

            building.ConstructionState = BuildingConstructionState.Operational;
            BuildingVisualFeed.Mark(building); // FG3-LOG-09：修复完成，画面重画这一座
            // ER3-SOFTLOCK-01：紧急救援机的免费修复（见 TryCreateEmergencyRepair）没有事务 id，
            // 玩家没有真正花过废料，InvestedScrap 保持 0——日后拆这栋楼返还 0，忠于"实际投入"字面。
            if (!string.IsNullOrEmpty(order.ResourceTransactionId))
            {
                // 记下这次真正花掉的废料，供日后"拆除按实际投入50%返还"使用——从 RepairProfile
                // 按建筑类型重新查一次（与创建订单时 Reserve 的数值来源相同的常量表，不会漂移），
                // 不需要反查 ResourceTransactionRecord 本体。
                if (HomeValleyLayout.RepairProfile.TryGetValue(building.BuildingTypeId, out (int ScrapCost, float Seconds) repairProfile))
                {
                    building.InvestedScrap = repairProfile.ScrapCost;
                }
                CampaignEconomyLedger.Commit(state, order.ResourceTransactionId);
            }
            HomeValleyPowerGrid.Recompute(state);
            order.State = WorkOrderState.Completed;
            MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId); // ER4-MCH-01：统计与经历唯一写入口。
            MarkAssignmentDirty();
            // ER8-CONTENT-01：完工音按建筑区分（BuildingCatalog.SfxId 的消费点）。
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BuildComplete, building.Position,
                Feedback.FeedbackCues.BuildingLabel(building.BuildingId) + "已修复",
                Feedback.FeedbackCues.BuildingTypeSfx(building.BuildingTypeId));
        }

        private static void CompleteBuild(CampaignState state, WorkOrderRecord order)
        {
            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == order.TargetId);
            if (building != null && !string.IsNullOrEmpty(building.RelocateFromId))
            {
                CompleteRelocation(state, order, building);
                return;
            }
            if (HomeValleyConstruction.IsBeltPlan(order.TargetId))
            {
                // FG3-LOG-02：传送带规划的格子已在施工中逐格进了内核，这里只收尾（规划从存档移除）。
                PlannedBeltRecord donePlan = HomeValleyConstruction.FindPlan(state, order.TargetId);
                int cells = HomeValleyConstruction.BuiltCells(donePlan);
                // FG3-LOG-04：分流器 / 合流器 / 地下传送带完工写它的名字，不写“传送带 N 格”。
                string doneText = donePlan != null && donePlan.NodeKind != 0
                    ? GameLogic.Localization.GameText.Format("build.piece.done", HomeValleyConstruction.PieceName(donePlan))
                    : GameLogic.Localization.GameText.Format("build.belt.done", cells.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Vector2 at = HomeValleyConstruction.SitePosition(state, order);
                HomeValleyConstruction.OnSiteCompleted(state, order, null);
                order.State = WorkOrderState.Completed;
                MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId);
                MarkAssignmentDirty();
                Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BuildComplete, at, doneText);
                return;
            }
            if (building == null)
            {
                order.State = WorkOrderState.Failed;
                order.FailureReason = "target-destroyed";
                MarkAssignmentDirty();
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Failure, GameLogic.Localization.GameText.Get("build.site.gone"));
                return;
            }

            building.ConstructionState = BuildingConstructionState.Operational;
            BuildingVisualFeed.Mark(building); // FG3-LOG-09：完工，画面重画这一座（虚影 → 建筑）
            // FG3-LOG-02：投入 = 运到现场、建进去的材料（= 所需材料）；拆除时全额返还这么多。
            HomeValleyConstruction.OnSiteCompleted(state, order, building);
            HomeValleyPowerGrid.Recompute(state);
            order.State = WorkOrderState.Completed;
            MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId); // ER4-MCH-01：统计与经历唯一写入口。
            MarkAssignmentDirty();
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BuildComplete, building.Position,
                Feedback.FeedbackCues.BuildingLabel(building.BuildingId) + "已建成",
                Feedback.FeedbackCues.BuildingTypeSfx(building.BuildingTypeId));
        }

        /// <summary>
        /// FG3-LOG-01（FGR-LOG-008）：搬迁施工完成——原建筑从原位置移除，搬迁目标虚影接过原建筑的 ID、类型、生命、施工状态、库存、队列、
        /// 电力优先级与投入（“保留原设置”），放在原建筑在记录数组里的位置（按类型查找的旧代码拿到的仍是同一座）。
        /// 原建筑在施工期间已经不在了（被突袭摧毁等）：搬迁作废，虚影移除，工单失败并提示（没有材料要退：搬迁不花材料）。
        /// </summary>
        private static void CompleteRelocation(CampaignState state, WorkOrderRecord order, BuildingRecord ghost)
        {
            BuildingRecord[] records = state.BuildingRecords;
            int sourceIndex = Array.FindIndex(records, b => b != null && b.BuildingId == ghost.RelocateFromId);
            if (sourceIndex < 0)
            {
                if (ghost.ConstructionDelivered > 0)
                {
                    // FG3-LOG-07：升级虚影已经运到现场的差额材料全额退回（原建筑在施工期间没了）。
                    HomeValleyConstruction.ReturnMaterials(state, ghost.Position, CampaignEconomyLedger.ResourceScrap, ghost.ConstructionDelivered,
                        order.WorkOrderId + ":refund");
                    ghost.ConstructionDelivered = 0;
                }
                state.BuildingRecords = records.Where(b => b.BuildingId != ghost.BuildingId).ToArray();
                order.State = WorkOrderState.Failed;
                order.FailureReason = "relocate-source-gone";
                MarkAssignmentDirty();
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Failure,
                    GameLogic.Localization.GameText.Get("grid.reason.relocate_source_gone"));
                return;
            }
            BuildingRecord source = records[sourceIndex];
            bool upgrade = Grid.HomeGridService.IsUpgradeGhost(ghost);
            string fromType = source.BuildingTypeId;
            var moved = new BuildingRecord
            {
                BuildingId = source.BuildingId,
                // FG3-LOG-07：升级虚影带着新类型（搬迁虚影的类型与原建筑相同）。
                BuildingTypeId = ghost.BuildingTypeId,
                RegionId = source.RegionId,
                Position = ghost.Position,
                Rotation = ghost.Rotation,
                GridX = ghost.GridX,
                GridY = ghost.GridY,
                Health = source.Health,
                // 原样带过原建筑的运行状态（运转 / 玩家关停 / 受损），不替玩家重新启用（FGR-BASE-020）；
                // 其它状态（搬迁期间不该出现）按运转处理。
                ConstructionState = source.ConstructionState == BuildingConstructionState.Damaged
                                    || source.ConstructionState == BuildingConstructionState.Disabled
                    ? source.ConstructionState
                    : BuildingConstructionState.Operational,
                PowerPriority = source.PowerPriority,
                PowerState = source.PowerState,
                Inventory = source.Inventory ?? Array.Empty<CargoEntry>(),
                QueueIds = source.QueueIds ?? Array.Empty<string>(),
                BlockedReason = source.BlockedReason,
                // FG3-LOG-07：升级把差额建了进去，拆除时连同差额全额返还。
                InvestedScrap = source.InvestedScrap + Math.Max(0, ghost.ConstructionRequired),
                RelocateFromId = null,
            };
            var next = new List<BuildingRecord>(records.Length);
            for (int i = 0; i < records.Length; i++)
            {
                if (i == sourceIndex)
                {
                    next.Add(moved);
                }
                else if (records[i] == null || records[i].BuildingId != ghost.BuildingId)
                {
                    next.Add(records[i]);
                }
            }
            state.BuildingRecords = next.ToArray();
            HomeValleyPowerGrid.Recompute(state);
            order.State = WorkOrderState.Completed;
            MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId);
            MarkAssignmentDirty();
            Grid.HomeGridService.OnRelocationCompleted(state, moved);
            HomeValleyConstruction.Touch();
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BuildComplete, moved.Position,
                upgrade
                    ? GameLogic.Localization.GameText.Format("plan.upgrade.done", Grid.HomeGridService.DisplayName(fromType), Grid.HomeGridService.DisplayName(moved.BuildingTypeId))
                    : GameLogic.Localization.GameText.Format("ui.build.relocated_done", Grid.HomeGridService.DisplayName(moved.BuildingTypeId)),
                Feedback.FeedbackCues.BuildingTypeSfx(moved.BuildingTypeId));
        }

        /// <summary>Salvage 的完成分支路由：残骸节点走既有 ER3-STO-01 逻辑，建筑（ER3-SOFTLOCK-01
        /// 拆除）走新分支。两者用 TargetId 的字符串格式天然区分——残骸节点固定是
        /// <see cref="HomeValleyLayout.Wreckage1NodeId"/>/<see cref="HomeValleyLayout.Wreckage2NodeId"/>
        /// 这两个常量，建筑 ID 恒为 "region:type" 格式，两个命名空间不会碰撞，不需要在
        /// <see cref="WorkOrderRecord"/> 上加一个额外字段区分"这是哪种 Salvage"。</summary>
        private static void CompleteSalvage(CampaignState state, WorkOrderRecord order)
        {
            if (order.TargetId == HomeValleyLayout.Wreckage1NodeId || order.TargetId == HomeValleyLayout.Wreckage2NodeId)
            {
                CompleteWreckageSalvage(state, order);
                return;
            }
            CompleteDemolish(state, order);
        }

        private static void CompleteWreckageSalvage(CampaignState state, WorkOrderRecord order)
        {
            RegionRecord region = state.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId);
            if (region == null || (region.DestroyedNodeIds != null && region.DestroyedNodeIds.Contains(order.TargetId)))
            {
                order.State = WorkOrderState.Completed; // 幂等：另一条路径已经拆过（不应发生，但不留幽灵订单）。
                MarkAssignmentDirty();
                return;
            }

            region.DestroyedNodeIds = (region.DestroyedNodeIds ?? Array.Empty<string>()).Append(order.TargetId).ToArray();

            Vector2 dropPosition = order.TargetId == HomeValleyLayout.Wreckage1NodeId
                ? HomeValleyLayout.Wreckage1.Position
                : HomeValleyLayout.Wreckage2.Position;
            HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, dropPosition,
                CampaignEconomyLedger.ResourceScrap, HomeValleyLayout.WreckageScrapYield, order.TargetId + ":salvage-drop");

            GroundItemRecord dropped = HomeValleyCargo.FindGroundItemBySalvageId(state, order.TargetId + ":salvage-drop");
            if (dropped != null)
            {
                HomeValleyCargo.HaulTicket ticket = HomeValleyCargo.TryReserveHaul(state, dropped.GroundItemId);
                HomeValleyCargo.CommitHaul(state, ticket, order.ResourceTransactionId);
            }

            order.State = WorkOrderState.Completed;
            MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId); // ER4-MCH-01：统计与经历唯一写入口。
            MarkAssignmentDirty();
        }

        /// <summary>ER3-SOFTLOCK-01 AC-ECO-012：建筑真正从 <see cref="CampaignState.BuildingRecords"/>
        /// 移除（"拆除"是永久性的，不是变回 Damaged），产出走地面物+两阶段票据——与残骸拆解同一套
        /// "仓满就留在地面待收集"的诚实处理，不强行塞进仓库。</summary>
        /// <summary>拆除返还地面物的 salvage ID（每张拆除工单唯一）。</summary>
        public static string DemolishDropId(WorkOrderRecord order) => order.WorkOrderId + ":demolish-drop"; // 工单 ID 已含目标建筑 ID

        /// <summary>最近一次拆除返还的建筑材料与内部缓存数量（自检读）。</summary>
        public static int LastDemolishRefund { get; private set; }
        public static int LastDemolishCacheReturned { get; private set; }

        private static void CompleteDemolish(CampaignState state, WorkOrderRecord order)
        {
            BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == order.TargetId);
            if (building == null)
            {
                order.State = WorkOrderState.Failed;
                order.FailureReason = "target-destroyed";
                CampaignEconomyLedger.Cancel(state, order.ResourceTransactionId);
                MarkAssignmentDirty();
                return;
            }

            state.BuildingRecords = state.BuildingRecords.Where(b => b.BuildingId != building.BuildingId).ToArray();

            // FG3-LOG-02（FGR-LOG-007 全额返还）：建筑本身的材料（实际投入的全额）+ 建筑内部缓存的物品，送回仓库；放不下的变成地面物，
            // 生成搬运单等仓库有空间时由机器搬走。旧存档里预登记的“一半返还”生产事务作废（空退款），按全额结算。
            if (!string.IsNullOrEmpty(order.ResourceTransactionId))
            {
                CampaignEconomyLedger.Cancel(state, order.ResourceTransactionId);
            }
            // 地面物按 salvage ID 去重：带上工单 ID，同一建筑 ID 被重建再拆时，第二份返还不会被旧的地面物吞掉。
            string dropId = DemolishDropId(order);
            HomeValleyConstruction.ReturnMaterials(state, building.Position, CampaignEconomyLedger.ResourceScrap, building.InvestedScrap, dropId);
            LastDemolishRefund = building.InvestedScrap;
            LastDemolishCacheReturned = 0;
            if (building.Inventory != null)
            {
                for (int i = 0; i < building.Inventory.Length; i++)
                {
                    CargoEntry c = building.Inventory[i];
                    if (c.Amount <= 0 || string.IsNullOrEmpty(c.ResourceType))
                    {
                        continue;
                    }
                    HomeValleyConstruction.ReturnMaterials(state, building.Position, c.ResourceType, c.Amount,
                        dropId + ":cache:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    LastDemolishCacheReturned += c.Amount;
                }
            }

            HomeValleyPowerGrid.Recompute(state); // 拆掉一个电力消费者/供给者，电网必须重算。
            order.State = WorkOrderState.Completed;
            MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId); // ER4-MCH-01：统计与经历唯一写入口。
            MarkAssignmentDirty();
        }

        private static void TickRecharge(CampaignState state, WorkOrderRecord order, float dt)
        {
            if (!MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord machine))
            {
                order.State = WorkOrderState.Cancelled;
                order.FailureReason = "machine-not-found";
                MarkAssignmentDirty();
                return;
            }

            float max = HomeValleyLayout.BatteryCapacity.TryGetValue(machine.ChassisId, out float cap) ? cap : 100f;
            machine.Battery = Mathf.Min(max, machine.Battery + HomeValleyLayout.BatteryHomeChargeRatePerSecond * dt);
            if (machine.Battery >= max)
            {
                order.State = WorkOrderState.Completed;
                MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId); // ER4-MCH-01：统计与经历唯一写入口。
                MarkAssignmentDirty();
            }
        }

        /// <summary>被动恢复（DEMO-CONTENT-LOCK.md §2.4"被动恢复每秒1"），对区域内所有存活机器生效，
        /// 正在 InProgress-Recharge 的机器跳过（那条路径已经在用更快的家园充电速率推进，不重复叠加）。</summary>
        private static void TickBatteryRegen(CampaignState state, float dt)
        {
            if (state == null)
            {
                return;
            }
            var rechargingLogicIds = new HashSet<int>(
                (state.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                    .Where(o => o.Kind == WorkOrderKind.Recharge && o.State == WorkOrderState.InProgress)
                    .Select(o => o.AssignedMachineLogicId));

            foreach (MachineRecord machine in MachineRegistry.AllRecords)
            {
                if (machine.RegionId != HomeValleyLayout.RegionId || !machine.IsAlive || rechargingLogicIds.Contains(machine.LogicId))
                {
                    continue;
                }
                float max = HomeValleyLayout.BatteryCapacity.TryGetValue(machine.ChassisId, out float cap) ? cap : 100f;
                if (machine.Battery < max)
                {
                    machine.Battery = Mathf.Min(max, machine.Battery + HomeValleyLayout.BatteryPassiveRegenPerSecond * dt);
                }
            }
        }

        // ── 中断：机器死亡（ERD-WRK-003 第四条）────────────────────────────────────

        /// <summary>ERD-WRK-003 原文允许"订单回 Ready 或 Failed"两种实现选择，本类统一选 Failed——
        /// 不选 Ready 是刻意的：资源事务一旦退款（Cancel）就永久终结，不能再 Commit；如果订单回到
        /// Ready 又被重新指派，<see cref="OnArrivedAtWork"/>/<see cref="CompleteRepair"/> 等后续步骤会
        /// 对着一笔已终结的事务再次 MarkRunning/Commit——这些调用会静默失败（<see cref="CampaignEconomyLedger"/>
        /// 对终态事务的写操作直接拒绝，不抛异常），实际效果是"建筑/残骸被标记完工，但对应的废料
        /// 从未真正扣款/发放"，一个真实的账目漏洞。Failed 是终态，玩家必须重新点选目标发起全新订单
        /// （重新 Propose+Reserve 一笔干净的事务），彻底避免这类"复活旧事务"的漏洞类别，货舱掉落物/
        /// 已建 Planned 建筑的撤销仍然发生，机器没有白白损失任何已实际持有的东西。</summary>
        private static void HandleMachineDeath(CampaignState state, WorkOrderRecord order)
        {
            PathWatch.Remove(order.WorkOrderId);
            WaitingWatch.Remove(order.WorkOrderId);

            Vector2 dropPosition = MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord dead)
                ? dead.WorldPosition
                : Vector2.zero;

            if (order.Kind == WorkOrderKind.Build)
            {
                // FG3-LOG-02：施工的机器阵亡不作废玩家的虚影——货舱里这一趟的材料就地落地（生成搬运单），已到现场的材料与进度保留，
                // 施工单回到待分配池由别的机器接着干（施工单没有长期挂着的资源事务，不存在“复活旧事务”的漏洞）。
                ReleaseBuildMachine(state, order, dropOnly: true);
                order.Leg = HomeValleyConstruction.MaterialsStillNeeded(state, order) > 0 && order.State != WorkOrderState.InProgress ? 1 : order.Leg;
                order.State = WorkOrderState.Ready;
                order.FailureReason = null;
                order.AssignedMachineLogicId = 0;
                MarkAssignmentDirty();
                HomeValleyConstruction.Touch();
                return;
            }

            ReleaseResourcesAndCargo(state, order, dropPosition, refund: true);
            order.State = WorkOrderState.Failed;
            order.FailureReason = "machine-died";
            order.AssignedMachineLogicId = 0;
            MarkAssignmentDirty();
            // ER8-CONTENT-01 AC-AUD-001 失败：执行机器损失导致工单中止。
            string target = Feedback.FeedbackCues.BuildingLabel(order.TargetId);
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.Failure, dropPosition,
                (string.IsNullOrEmpty(target) ? "工单" : target + " 工单") + "中止：执行机器损失，资源已退还");
        }

        private static void ReleaseResourcesAndCargo(CampaignState state, WorkOrderRecord order, Vector2 dropPosition, bool refund)
        {
            if (order.Kind == WorkOrderKind.Haul
                && MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord machine)
                && machine.Cargo != null && machine.Cargo.Length > 0)
            {
                CargoEntry cargo = machine.Cargo[0];
                HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, dropPosition,
                    cargo.ResourceType, cargo.Amount, order.SourceId + ":redrop:" + order.WorkOrderId);
                machine.Cargo = Array.Empty<CargoEntry>();
            }

            if (refund && !string.IsNullOrEmpty(order.ResourceTransactionId))
            {
                CampaignEconomyLedger.Cancel(state, order.ResourceTransactionId);
            }

            if (order.Kind == WorkOrderKind.Build)
            {
                // FG3-LOG-02（FGR-LOG-006“取消虚影时，已经预留的材料全额退回”）：机器货舱里的这一趟与已运到现场的材料全部退回
                // （仓库放不下的变成地面物，机器之后搬走）；传送带规划里没建成的格子一并取消。
                ReleaseBuildMachine(state, order, dropOnly: false);
                HomeValleyConstruction.OnSiteCancelled(state, order);
                // 未建成的规划建筑一并撤销，不留"永久 Planned 幽灵建筑"。
                state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>())
                    .Where(b => b.BuildingId != order.TargetId)
                    .ToArray();
            }
        }
    }
}
