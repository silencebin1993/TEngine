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
        /// FG0-ARCH-04：在格网上已经校验过的位置生成“规划中”的建筑记录与新建工作单（唯一调用方 <see cref="Grid.HomeGridService.TryPlace"/>）。
        /// 废料在此预留（Propose → Reserve，完工 Commit，取消 Cancel 全额退回）。
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
            string txId = workOrderId + ":tx";
            CampaignEconomyLedger.ProposeConsume(state, txId, buildingId, CampaignEconomyLedger.ResourceScrap, profile.ScrapCost);
            CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, txId);
            if (!reserve.Success)
            {
                CampaignEconomyLedger.Cancel(state, txId);
                return WorkOrderOpResult.Fail($"insufficient-scrap:{reserve.FailureReason}");
            }

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
            };
            state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(planned).ToArray();

            var order = NewOrder(state, workOrderId, WorkOrderKind.Build, buildingId, machineLogicId,
                resourceTransactionId: txId, duration: profile.Seconds);
            if (machineLogicId == 0)
            {
                order.State = WorkOrderState.Ready; // 待分配池：空闲机器 0.5 秒内领取（ERD-WRK-002）。
            }
            Append(state, order);
            MarkAssignmentDirty();
            return WorkOrderOpResult.Ok(workOrderId);
        }

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
            string txId = workOrderId + ":tx";
            int refund = building.InvestedScrap / 2; // 向下取整；0 投入（开局即 Operational 的建筑）返还 0，忠于字面。
            CampaignEconomyLedger.ProposeProduce(state, txId, building.BuildingId, CampaignEconomyLedger.ResourceScrap, refund);
            CampaignEconomyLedger.Reserve(state, txId);

            var order = NewOrder(state, workOrderId, WorkOrderKind.Salvage, building.BuildingId, machineLogicId,
                resourceTransactionId: txId, duration: HomeValleyLayout.DemolishSeconds);
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
            if (order == null || order.State != WorkOrderState.Reserved)
            {
                return;
            }

            if (!string.IsNullOrEmpty(order.ResourceTransactionId))
            {
                CampaignEconomyLedger.MarkRunning(state, order.ResourceTransactionId);
            }
            PathWatch.Remove(order.WorkOrderId); // 离开 Reserved：赶路看门狗不再需要跟踪这条订单。
            order.State = WorkOrderState.InProgress;
            // 故意不把 Progress 清零：新订单本来就是 0（NewOrder 初始化），但机器受控中断后被重新指派
            // 续工的订单，Progress 是"已经干了多少"的真实进度（ERD-WRK-003"机器受控...保留已搬货物"
            // 同一条纪律延伸到工作进度本身，不是只保货物），这里清零会让续工的人白干一次已完成的部分。
        }

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
            order.State = WorkOrderState.Ready;
            order.AssignedMachineLogicId = 0;
            MarkAssignmentDirty(); // 订单立即回到分配池，另一台空闲机器不必等满 0.5 秒窗口。
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
        public static void Tick(CampaignState state, float dt, Func<int, Vector2?> getMachinePosition,
            Action<int> releaseMachineMovement, Func<int, bool> isDirectControlled, Action<WorkOrderRecord> beginAssignedMovement)
        {
            if (state == null)
            {
                return;
            }

            if (state.WorkOrders != null && state.WorkOrders.Length > 0)
            {
                foreach (WorkOrderRecord order in state.WorkOrders)
                {
                    if (IsTerminal(order.State))
                    {
                        continue;
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
                            TickReserved(state, order, dt, getMachinePosition, releaseMachineMovement);
                            break;
                        case WorkOrderState.InProgress:
                            TickInProgress(state, order, dt);
                            break;
                        case WorkOrderState.Waiting:
                            TickWaiting(state, order, dt);
                            break;
                    }
                }
            }

            TickBatteryRegen(state, dt);
            TickAssignment(state, dt, getMachinePosition, isDirectControlled, beginAssignedMovement);
        }

        // ── ER3-WRK-02：确定性自动分配 ───────────────────────────────────────────

        /// <summary>脏事件标记——不落盘（进程重启后最坏情况是多等一次 0.5 秒窗口，不是正确性问题，
        /// 同 <see cref="PathWatch"/> 一类瞬态记忆的既定处理方式）。订单完成/取消/死亡/回 Ready 时
        /// 由本类各终止路径自行标记；玩家改动机器工作偏好由调用方（UI）在写入
        /// <see cref="MachineRegistry.TrySetWorkPriority"/> 成功后调用本方法。</summary>
        private static bool _assignDirty = true;
        private static float _assignTimer;
        private const float AssignIntervalSeconds = 0.5f;

        public static void MarkAssignmentDirty()
        {
            _assignDirty = true;
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
                (idleMachines ??= new List<MachineRecord>()).Add(machine);
            }
            if (idleMachines == null)
            {
                return;
            }
            idleMachines.Sort((a, b) => a.LogicId.CompareTo(b.LogicId));

            foreach (MachineRecord machine in idleMachines)
            {
                WorkOrderRecord best = null;
                int bestCategoryPriority = 0;
                float bestDistance = 0f;
                Vector2 fromPos = getMachinePosition?.Invoke(machine.LogicId) ?? machine.WorldPosition;

                foreach (WorkOrderRecord order in readyOrders)
                {
                    if (order.AssignedMachineLogicId != 0)
                    {
                        continue; // 本轮已被排在前面（LogicId 更小）的机器领走。
                    }
                    if (!IsCapable(machine.ChassisId, order.Kind))
                    {
                        continue;
                    }
                    int categoryPriority = machine.WorkPriorities?.Get(order.Kind) ?? 0;
                    if (categoryPriority <= 0)
                    {
                        continue; // 0＝该机器对这一类工作永久禁用（不影响玩家直接点选下令）。
                    }

                    float distance = Vector2.Distance(fromPos, ResolveWorkPosition(state, order));
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
        /// createdTick 小者优先 → 估算路径短者优先 → workOrderId 字典序小者优先（最终稳定平局判定）。
        /// 全部输入都是落盘字段或本帧计算的确定值，不含墙上时间或容器枚举顺序，保证 AC-WRK-001
        /// "同一订单集分配一致"。</summary>
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
            return string.CompareOrdinal(candidate.WorkOrderId, best.WorkOrderId) < 0;
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
            Func<int, Vector2?> getMachinePosition, Action<int> releaseMachineMovement)
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
                    BuildingRecord repairTarget = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == order.TargetId);
                    return repairTarget?.Position ?? Vector2.zero;
                case WorkOrderKind.Build:
                    // FG0-ARCH-04：建造位由格网决定（规划中的建筑记录的占地中心）。此前恒返回发电机2 的坑位，
                    // 信标与自由放置的建筑会让机器走错地方。
                    BuildingRecord buildTarget = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == order.TargetId);
                    return buildTarget?.Position ?? HomeValleyLayout.Generator2Site.Position;
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
                    BuildingRecord demolishTarget = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == order.TargetId);
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

        private static void TickInProgress(CampaignState state, WorkOrderRecord order, float dt)
        {
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
            if (order.FailureReason == "path-blocked")
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
                case WorkOrderKind.Build:
                    CompleteBuild(state, order);
                    break;
                case WorkOrderKind.Salvage:
                    CompleteSalvage(state, order);
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
            if (building == null)
            {
                order.State = WorkOrderState.Failed;
                order.FailureReason = "target-destroyed";
                CampaignEconomyLedger.Cancel(state, order.ResourceTransactionId);
                MarkAssignmentDirty();
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Failure, "建造工单失败：规划地块已不存在，废料已退还");
                return;
            }

            building.ConstructionState = BuildingConstructionState.Operational;
            if (HomeValleyLayout.BuildProfile.TryGetValue(building.BuildingTypeId, out (int ScrapCost, float Seconds) buildProfile))
            {
                building.InvestedScrap = buildProfile.ScrapCost;
            }
            CampaignEconomyLedger.Commit(state, order.ResourceTransactionId);
            HomeValleyPowerGrid.Recompute(state);
            order.State = WorkOrderState.Completed;
            MachineRegistry.RecordJobCompleted(order.AssignedMachineLogicId); // ER4-MCH-01：统计与经历唯一写入口。
            MarkAssignmentDirty();
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BuildComplete, building.Position,
                Feedback.FeedbackCues.BuildingLabel(building.BuildingId) + "已建成",
                Feedback.FeedbackCues.BuildingTypeSfx(building.BuildingTypeId));
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

            // 地面物按 salvage ID 去重：带上工单 ID，同一建筑 ID 被重建再拆时，第二份返还不会被旧的地面物吞掉。
            string dropId = DemolishDropId(order);
            HomeValleyCargo.SpawnGroundItem(state, HomeValleyLayout.RegionId, building.Position,
                CampaignEconomyLedger.ResourceScrap, building.InvestedScrap / 2, dropId);

            GroundItemRecord dropped = HomeValleyCargo.FindGroundItemBySalvageId(state, dropId);
            if (dropped != null)
            {
                HomeValleyCargo.HaulTicket ticket = HomeValleyCargo.TryReserveHaul(state, dropped.GroundItemId);
                HomeValleyCargo.CommitHaul(state, ticket, order.ResourceTransactionId);
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
                // 未建成的规划建筑一并撤销，不留"永久 Planned 幽灵建筑"。
                state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>())
                    .Where(b => b.BuildingId != order.TargetId)
                    .ToArray();
            }
        }
    }
}
