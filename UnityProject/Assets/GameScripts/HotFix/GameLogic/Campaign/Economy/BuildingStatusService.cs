using System;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-05（FGR-ECO-010；FG00 B05）：所有建筑共用的状态。数值与 <c>UI.Kit.UiEntityStatus</c> 一一对应（形状 + 颜色 + 名称），
    /// <see cref="Building"/> 是本 Story 补的“建造中”。
    /// </summary>
    public enum BuildingStatusKind : byte
    {
        Working = 0,
        Idle = 1,
        NoMaterial = 2,
        NoPower = 3,
        NoFluid = 4,
        OutputBlocked = 5,
        Disabled = 6,
        /// <summary>受损：耐久没满，照常工作（FGR-ECO-013 效率不变）。</summary>
        Damaged = 7,
        /// <summary>已摧毁：只剩虚影（<see cref="BuildingConstructionState.Damaged"/>），可以重建。</summary>
        Destroyed = 8,
        Upgrading = 9,
        Building = 10,
    }

    /// <summary>一座建筑此刻的通用状态与原因（原因写明是什么、怎么办，B06）。</summary>
    public readonly struct BuildingStatus
    {
        public readonly BuildingStatusKind Kind;
        /// <summary>原因的种类（自检按它断言“显示了正确原因”，不按文字）。</summary>
        public readonly string ReasonCode;
        public readonly string Reason;

        public BuildingStatus(BuildingStatusKind kind, string reasonCode, string reason)
        {
            Kind = kind;
            ReasonCode = reasonCode;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// FG4-ECO-05（FGR-ECO-010 通用状态；FGR-LOG-082 原因）：任何一座建筑的状态——建造中、工作、待机、缺料、缺流体、缺电、输出堵塞、禁用、受损、已摧毁、升级中。
    /// 只读：从既有状态组件（建造状态、电网仲裁、生产建筑状态、装配 / 解析队列、储能）汇总，不另存状态，不推进任何东西。
    /// 优先级：虚影 → 已摧毁 → 升级中 → 禁用 → 功能状态（生产 / 电力 / 队列）；功能上“工作 / 待机”而耐久没满时显示“受损”（照常工作）。
    /// 开销 O(1)（生产建筑查索引、电网查实体表）；面板 / 悬停按需调用，没有每帧遍历。
    /// </summary>
    public static class BuildingStatusService
    {
        public static BuildingStatus Evaluate(CampaignState state, BuildingRecord b)
        {
            if (b == null)
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "none", string.Empty);
            }
            if (HomeGridService.IsRelocationGhost(b) || HomeValleyController.IsPlannedGhost(b))
            {
                return new BuildingStatus(BuildingStatusKind.Building, "ghost", GhostReason(state, b));
            }
            if (b.ConstructionState == BuildingConstructionState.Damaged || b.ConstructionState == BuildingConstructionState.Destroyed)
            {
                int scrap = BuildingOps.RebuildScrapFor(b);
                return new BuildingStatus(BuildingStatusKind.Destroyed, "destroyed",
                    scrap >= 0 ? GameText.Format("bs.reason.destroyed", scrap) : GameText.Get("bs.reason.destroyed_norepair"));
            }
            BuildingStatus functional = Functional(state, b);
            if (state != null && HomeGridService.FindRelocationGhost(state, b.BuildingId) is BuildingRecord ghost && HomeGridService.IsUpgradeGhost(ghost))
            {
                WorkOrderRecord order = HomeValleyWorkOrders.FindActiveBuild(state, ghost.BuildingId);
                string progress = order != null ? HomeValleyConstruction.DescribeStatus(state, order) : string.Empty;
                string running = GameText.Get(BuildingOps.UiStatusNameKey(functional.Kind));
                string reason = ghost.Tier > 0
                    ? GameText.Format("bs.reason.upgrading", ghost.Tier, progress, running)
                    : GameText.Format("bs.reason.upgrading_type", HomeGridService.DisplayName(ghost.BuildingTypeId), progress, running);
                return new BuildingStatus(BuildingStatusKind.Upgrading, "upgrading", reason);
            }
            if (b.ConstructionState == BuildingConstructionState.Disabled)
            {
                return new BuildingStatus(BuildingStatusKind.Disabled, "disabled", GameText.Get("bs.reason.disabled"));
            }
            if (BuildingOps.IsWorn(b))
            {
                int dur = Mathf.RoundToInt(BuildingOps.Durability(b));
                int max = Mathf.RoundToInt(BuildingOps.MaxDurability(b.BuildingTypeId));
                if (functional.Kind == BuildingStatusKind.Working || functional.Kind == BuildingStatusKind.Idle)
                {
                    return new BuildingStatus(BuildingStatusKind.Damaged, "worn", GameText.Format("bs.reason.worn", dur, max, BuildingOps.RepairKitsFor(b)));
                }
                return new BuildingStatus(functional.Kind, functional.ReasonCode, GameText.Format("bs.reason.worn_then", functional.Reason, dur, max));
            }
            return functional;
        }

        /// <summary>一行文字：“● 工作中 · 原因”（形状符号由界面画；文字里不再重复颜色）。</summary>
        public static string Line(CampaignState state, BuildingRecord b)
        {
            BuildingStatus s = Evaluate(state, b);
            return GameText.Format("bs.line", BuildingOps.NameOf(b), GameText.Get(BuildingOps.UiStatusNameKey(s.Kind)), s.Reason.Replace("\n", " · "));
        }

        private static string GhostReason(CampaignState state, BuildingRecord b)
        {
            WorkOrderRecord order = state != null ? HomeValleyWorkOrders.FindActiveBuild(state, b.BuildingId) : null;
            if (order == null)
            {
                return GameText.Format("bs.reason.ghost_unassigned", Core.InputDisplay.ForAction(Core.GameActionId.ConstructionQueue));
            }
            return GameText.Format("bs.reason.ghost", HomeValleyConstruction.DescribeStatus(state, order));
        }

        /// <summary>FG4-ECO-11：超控阵列缺电时在原因后面补一行“信号核第 3～4 槽失效（固件保留，不生效）”；别的建筑为空串。</summary>
        private static string OverrideOfflineSuffix(BuildingRecord b)
        {
            if (b == null || b.BuildingTypeId != Signal.OverrideArrayService.TypeId)
            {
                return string.Empty;
            }
            int initial = Signal.SignalCoreService.InitialSlots;
            return "\n" + GameText.Format("bs.reason.override_offline", Signal.OverrideArrayService.SlotsText(initial + 1, initial + BuildingOps.TierOf(b)));
        }

        /// <summary>功能状态（建成且没禁用时）。</summary>
        private static BuildingStatus Functional(CampaignState state, BuildingRecord b)
        {
            if (state != null && ProductionService.TryGet(state, b.BuildingId, out ProductionService.Producer p))
            {
                if (p.State == ProdState.None)
                {
                    ProductionService.TryDescribe(state, b, out _); // 还没推进过一步：按建筑状态给出状态（不推进任何东西）。
                }
                return new BuildingStatus(FromProd(p.State), "prod." + p.Reason, ProductionService.ReasonText(state, p));
            }
            string type = b.BuildingTypeId;
            // 用电建筑没吃到电：缺电（未接入 / 按优先级停）。
            if (BuildingOps.HasPriority(type))
            {
                if (b.PowerState == BuildingPowerState.Unpowered)
                {
                    return new BuildingStatus(BuildingStatusKind.NoPower, "power.unconnected",
                        GameText.Format("prod.reason.no_power", GameText.Get("prod.reason.power_unconnected")) + OverrideOfflineSuffix(b));
                }
                if (b.PowerState == BuildingPowerState.Brownout)
                {
                    return new BuildingStatus(BuildingStatusKind.NoPower, "power.brownout",
                        GameText.Format("prod.reason.no_power", GameText.Format("prod.reason.power_brownout", b.PowerPriority)) + OverrideOfflineSuffix(b));
                }
            }
            if (type == HomeValleyLayout.BuildingTypeCore)
            {
                float baseSupply = HomeValleyLayout.BaseCoreSupply;
                string coreReason = GameText.Format("bs.reason.working_core", HomeValleyPowerGrid.Num(baseSupply));
                // FG4-ECO-10（FGR-ECO-070；B05）：核心正在应急产废料 / 今天的应急打印已用掉时写在核心状态里（进度、下一件 / 下一台什么时候来）。
                string scrapLine = SoftlockService.CoreScrapStatus(state);
                string printLine = SoftlockService.PrintStatus(state);
                if (scrapLine != null || printLine != null)
                {
                    string extra = string.Join("\n", new[] { scrapLine, printLine }.Where(x => x != null));
                    return new BuildingStatus(BuildingStatusKind.Working, scrapLine != null ? "core.emergency_scrap" : "core.print_wait", coreReason + "\n" + extra);
                }
                return new BuildingStatus(BuildingStatusKind.Working, "core", coreReason);
            }
            if (BuildingOps.IsWarehouse(b))
            {
                int scrapCap = ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef scrap) ? HomeInventory.Capacity(state, scrap) : 0;
                return new BuildingStatus(BuildingStatusKind.Working, "warehouse",
                    GameText.Format("bs.reason.working_warehouse", Math.Max(0, state?.Scrap ?? 0), scrapCap, BuildingOps.WarehouseTierCapacity(b)));
            }
            if (type == HomeValleyLayout.BuildingTypeSignalTower)
            {
                float r = BuildingOps.TierOf(b) >= 2 ? Signal.SignalCoverageService.TowerT2Radius : Signal.SignalCoverageService.TowerRadius;
                return new BuildingStatus(BuildingStatusKind.Working, "signal", GameText.Format("bs.reason.working_signal", Mathf.RoundToInt(r)));
            }
            if (type == Signal.OverrideArrayService.TypeId)
            {
                // FG4-ECO-11：超控阵列运转中——写明解锁了信号核哪几个槽、耗电多少（按等级）。
                int initial = Signal.SignalCoreService.InitialSlots;
                return new BuildingStatus(BuildingStatusKind.Working, "override",
                    GameText.Format("bs.reason.working_override", Signal.OverrideArrayService.SlotsText(initial + 1, initial + BuildingOps.TierOf(b)),
                        HomeValleyPowerGrid.Num(HomeValleyPowerGrid.DemandOf(b))));
            }
            if (type == IntelCatalog.TypeId)
            {
                // FG5-RND-05：监听站——破译中（哪一类、进度、几座、速度）/ 空闲（现有情报都有效）。缺电 / 禁用 / 被毁由上面的通用状态先报。
                return IntelService.StatusOf(state, b);
            }
            if (type == BlackBoxService.TypeId)
            {
                // FG5-RND-06：黑匣子陈列馆——分析中（谁的黑匣子、进度、已入账的技术数据、还有几个排队）/ 空闲。缺电 / 禁用 / 被毁由上面的通用状态先报。
                return BlackBoxService.StatusOf(state, b);
            }
            if (type == FusionCatalog.TypeId)
            {
                // FG5-RND-04：电路合成台——熔合中（产物、进度）/ 空闲（怎么开始）。缺电 / 禁用 / 被毁由上面的通用状态先报。
                return FusionService.StatusOf(state, b);
            }
            if (type == TestRangeCatalog.TypeId)
            {
                // FG5-RND-03：靶场——测试中（投影数、时长、每秒伤害）/ 空闲（怎么开始）。
                if (TestRangeService.IsRunning(b.BuildingId))
                {
                    RangeResultRecord live = TestRangeService.LiveReadings(b.BuildingId);
                    return new BuildingStatus(BuildingStatusKind.Working, "range.running", GameText.Format("bs.reason.range_running",
                        TestRangeService.ProjectionsOf(b.BuildingId).Count, Mathf.RoundToInt(live?.Seconds ?? 0f), TestRangeService.Num(live?.Dps ?? 0f)));
                }
                return new BuildingStatus(BuildingStatusKind.Idle, "range.idle", GameText.Get("bs.reason.range_idle"));
            }
            if (type == HomeValleyLayout.BuildingTypeSignalRelay)
            {
                return new BuildingStatus(BuildingStatusKind.Working, "relay",
                    GameText.Format("bs.reason.working_relay", Mathf.RoundToInt(Signal.SignalCoverageService.RelayTowerRadius)));
            }
            if (HomeValleyPowerGrid.IsStorageType(type))
            {
                if (HomeValleyPowerGrid.TryGetStorage(state, b.BuildingId, out double stored, out double cap))
                {
                    return new BuildingStatus(BuildingStatusKind.Working, "storage",
                        GameText.Format("bs.reason.working_storage", Mathf.RoundToInt((float)stored), Mathf.RoundToInt((float)cap)));
                }
                return new BuildingStatus(BuildingStatusKind.OutputBlocked, "supply.unconnected", GameText.Get("bs.reason.unconnected_supply"));
            }
            if (HomeValleyLayout.PowerSupplyProfile.ContainsKey(type))
            {
                if (!HomeValleyPowerGrid.IsConnected(state, b.BuildingId))
                {
                    return new BuildingStatus(BuildingStatusKind.OutputBlocked, "supply.unconnected", GameText.Get("bs.reason.unconnected_supply"));
                }
                float out0 = HomeValleyPowerGrid.AvailableSupplyOf(state, b.BuildingId);
                if (out0 <= 0f)
                {
                    return new BuildingStatus(BuildingStatusKind.Idle, "supply.dark", GameText.Format("bs.reason.idle_supply", GameText.Get("bs.reason.idle_supply_dark")));
                }
                return new BuildingStatus(BuildingStatusKind.Working, "supply", GameText.Format("bs.reason.working_supply", HomeValleyPowerGrid.Num(HomeValleyPowerGrid.OutputOf(state, b.BuildingId))));
            }
            if (HomeValleyPowerGrid.IsPoleType(type))
            {
                if (!HomeValleyPowerGrid.TryGetBuildingPower(state, b.BuildingId, out BuildingPowerInfo info) || info.Subnet < 0)
                {
                    return new BuildingStatus(BuildingStatusKind.Idle, "pole.unconnected", GameText.Get("bs.reason.unconnected_pole"));
                }
                return new BuildingStatus(BuildingStatusKind.Working, "pole", GameText.Format("bs.reason.working_pole", HomeValleyPowerGrid.DescribeSubnetLine(info.Subnet)));
            }
            if (AssemblyMaterials.IsStation(b))
            {
                return AssemblyStatus(state, b);
            }
            if (type == ResearchService.TypeId)
            {
                return ResearchService.LabStatus(state, b); // FG5-RND-01：仿真实验室（转换中 / 技术数据不足 / 缺电 / 禁用）
            }
            if (type == HomeValleyLayout.BuildingTypeAnalysisBench)
            {
                return HomeValleyAnalysis.Status(state, b); // FG5-RND-02：解析中 / 处理残骸 / 缺电（进度保留）/ 空闲“没有待解析的物品”
            }
            if (type == HomeValleyLayout.BuildingTypeRepairBay)
            {
                // FG4-ECO-06：机器维修规则派来的机器正在修 = 工作中（写明几台）。
                int repairing = 0;
                foreach (WorkOrderRecord o in state?.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                {
                    if (o != null && o.Kind == WorkOrderKind.MachineRepair && o.TargetId == b.BuildingId && o.State == WorkOrderState.InProgress)
                    {
                        repairing++;
                    }
                }
                return repairing > 0
                    ? new BuildingStatus(BuildingStatusKind.Working, "repair_bay.working", GameText.Format("bs.reason.working_repair_bay", repairing))
                    : new BuildingStatus(BuildingStatusKind.Idle, "repair_bay.idle", GameText.Get("bs.reason.idle_repair_bay"));
            }
            return new BuildingStatus(BuildingStatusKind.Working, "generic", GameText.Get("bs.reason.working_generic"));
        }

        private static BuildingStatus AssemblyStatus(CampaignState state, BuildingRecord b)
        {
            FactoryQueueItemRecord head = null;
            foreach (FactoryQueueItemRecord q in state?.FactoryQueues ?? Array.Empty<FactoryQueueItemRecord>())
            {
                if (q == null || q.State == FactoryQueueState.Completed || q.State == FactoryQueueState.Cancelled || q.State == FactoryQueueState.Failed)
                {
                    continue;
                }
                if (head == null || q.State == FactoryQueueState.Running || q.State == FactoryQueueState.OutputBlocked)
                {
                    head = q;
                    if (q.State == FactoryQueueState.Running || q.State == FactoryQueueState.OutputBlocked)
                    {
                        break;
                    }
                }
            }
            if (head == null)
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "assembly.idle", GameText.Get("bs.reason.idle_assembly"));
            }
            switch (head.State)
            {
                case FactoryQueueState.OutputBlocked:
                    return new BuildingStatus(BuildingStatusKind.OutputBlocked, "assembly.blocked", GameText.Get("bs.reason.blocked_assembly"));
                case FactoryQueueState.WaitingResources:
                    return new BuildingStatus(BuildingStatusKind.NoMaterial, "assembly.waiting",
                        GameText.Format("bs.reason.waiting_assembly", HomeValleyFactory.DescribeWait(head) ?? HomeValleyFactory.DescribeFailure(head.BlockedReason)));
                case FactoryQueueState.WaitingPower:
                    return new BuildingStatus(BuildingStatusKind.NoPower, "assembly.power",
                        GameText.Format("prod.reason.no_power", GameText.Get("prod.reason.power_unconnected")));
                case FactoryQueueState.Running:
                    int pct = head.Duration > 0f ? Mathf.RoundToInt(Mathf.Clamp01(head.Progress / head.Duration) * 100f) : 0;
                    return new BuildingStatus(BuildingStatusKind.Working, "assembly.running",
                        GameText.Format("bs.reason.working_assembly", HomeValleyFactory.BlueprintDisplayName(state, head.BlueprintId), pct.ToString(CultureInfo.InvariantCulture)));
                default:
                    return new BuildingStatus(BuildingStatusKind.Idle, "assembly.queued", GameText.Get("bs.reason.idle_assembly"));
            }
        }

        public static BuildingStatusKind FromProd(ProdState s)
        {
            switch (s)
            {
                case ProdState.Working: return BuildingStatusKind.Working;
                case ProdState.MissingInput: return BuildingStatusKind.NoMaterial;
                case ProdState.NoResource: return BuildingStatusKind.NoMaterial;
                case ProdState.MissingFluid: return BuildingStatusKind.NoFluid;
                case ProdState.NoPower: return BuildingStatusKind.NoPower;
                case ProdState.OutputBlocked: return BuildingStatusKind.OutputBlocked;
                case ProdState.Disabled: return BuildingStatusKind.Disabled;
                case ProdState.Damaged: return BuildingStatusKind.Destroyed;
                case ProdState.Building: return BuildingStatusKind.Building;
                default: return BuildingStatusKind.Idle;
            }
        }
    }
}
