using System.Collections.Generic;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：8 建筑（DEMO-CONTENT-LOCK.md §2.1/§2.2 与目录 §3 隐含的"归还核心/
    /// 发电机/仓库/装配站/解析台/维修台/信号塔/导航信标"）。Key 用本类自己的 Id（对照
    /// <see cref="HomeValleyLayout.BuildingTypeGenerator"/> 等已存在的 BuildingTypeId 字符串），
    /// 与 <see cref="HomeValleyLayout.BuildingTypeGenerator2"/>（第二座发电机，ER3-WRK-01 新增的独立
    /// BuildingTypeId）共用同一条"发电机"内容——两者是同一机械内容的两个实例，不是两种内容，见
    /// <see cref="ResolveByBuildingTypeId"/>。</summary>
    public static class BuildingCatalog
    {
        public const string CoreId = "building_core";
        public const string GeneratorId = "building_generator";
        public const string WarehouseId = "building_warehouse";
        public const string AssemblyStationId = "building_assembly_station";
        public const string AnalysisBenchId = "building_analysis_bench";
        public const string RepairBayId = "building_repair_bay";
        public const string SignalTowerId = "building_signal_tower";
        public const string BeaconId = "building_beacon";

        private static readonly Dictionary<string, MechanicalContentDef> _defs = new Dictionary<string, MechanicalContentDef>
        {
            [CoreId] = new MechanicalContentDef
            {
                Id = CoreId,
                Category = MechanicalContentCategory.Building,
                DisplayName = "归还核心",
                Description = "战役唯一不可摧毁前提建筑；核心被毁进入失败界面。",
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "开局已存在，Operational，不可修复材料前置、不可关停（DEMO-CONTENT-LOCK.md §2.1/§2.2）。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "HP500，基础电力20，应急缓存180/180废料，基础带宽3（DEMO-CONTENT-LOCK.md §2.1）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_core",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyLayout.BaseCoreSupply/CoreCacheCapacity + HomeValleySoftlockGuard（核心被毁失败界面，ER3-SOFTLOCK-01 已验证）",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_building_core",
                SaveCompatible = true,
                LockedHintText = "开局已存在，无需解锁。",
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分（红/黄/橙/绿/灰），类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [GeneratorId] = new MechanicalContentDef
            {
                Id = GeneratorId,
                Category = MechanicalContentCategory.Building,
                DisplayName = "发电机",
                Description = "供电建筑；Operational 后提供额外电力容量，可建造第二座。",
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "开局 Damaged，修复30废料/20秒；可另建第二座（60废料/40秒），两者是同一内容的两个实例（DEMO-CONTENT-LOCK.md §2.1/§2.2）。",
                Slot = "建筑",
                ScrapCost = 30,
                Load = 0,
                ValuesSummary = "修复后额外电力80/座；ER3-WRK-01 已支持同时存在两座（generator + generator_2）（DEMO-CONTENT-LOCK.md §2.1/§2.2）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_generator",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyWorkOrders.TryCreateRepair/TryCreateBuild + HomeValleyPowerGrid.Recompute — 已在 ER3-PWR-01/WRK-01 用真实 Play Mode 验收",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_building_generator",
                SaveCompatible = true,
                LockedHintText = "开局已存在（Damaged），修复后 Operational。",
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [WarehouseId] = new MechanicalContentDef
            {
                Id = WarehouseId,
                Category = MechanicalContentCategory.Building,
                DisplayName = "仓库",
                Description = "扩充核心应急缓存之外的存储容量，可接收远征卸货。",
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "开局 Damaged，修复10废料/10秒（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 10,
                Load = 0,
                ValuesSummary = "Operational 后容量300，可接收远征卸货（DEMO-CONTENT-LOCK.md §2.1）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_warehouse",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyCargo.GetStorageCapacity — 已在 ER3-STO-01 用真实存读档/仓满场景验证",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_building_warehouse",
                SaveCompatible = true,
                LockedHintText = "开局已存在（Damaged），修复后 Operational。",
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [AssemblyStationId] = new MechanicalContentDef
            {
                Id = AssemblyStationId,
                Category = MechanicalContentCategory.Building,
                DisplayName = "装配站",
                Description = "生产与改造机器的建筑；本 Story 只交付内容目录，正式生产队列见 ER4-FAC-01。",
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "建筑已存在；有足额电力即 Operational，无额外修复材料前置（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "Operational 电力需求25，默认优先级2（DEMO-CONTENT-LOCK.md §2.2）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_assembly",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyPowerGrid.Recompute（供电已实装）；生产队列 UI/流程见 ER4-FAC-01",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_building_assembly",
                SaveCompatible = true,
                LockedHintText = "开局已存在，有电即 Operational。",
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-02",
            },
            [AnalysisBenchId] = new MechanicalContentDef
            {
                Id = AnalysisBenchId,
                Category = MechanicalContentCategory.Building,
                DisplayName = "解析台",
                Description = "解析带回的静默/铸造技术模块，解锁新内容；通电后也解锁维修悬浮生产。",
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "建筑已存在；有足额电力即 Operational，无额外修复材料前置（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "Operational 电力需求15，默认优先级2（DEMO-CONTENT-LOCK.md §2.2）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_analysis",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyPowerGrid.Recompute（供电已实装）；解析流程 UI 见 ER6-ANA-01",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_building_analysis",
                SaveCompatible = true,
                LockedHintText = "开局已存在，有电即 Operational。",
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-17",
            },
            [RepairBayId] = new MechanicalContentDef
            {
                Id = RepairBayId,
                Category = MechanicalContentCategory.Building,
                DisplayName = "维修台",
                Description = "维修悬浮专业机可修机器的建筑基点；不是开局修建筑的前置。",
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "建筑已存在；有足额电力即 Operational，无额外修复材料前置（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "Operational 电力需求15，默认优先级3（DEMO-CONTENT-LOCK.md §2.2）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_repair",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyPowerGrid.Recompute + HomeValleyWorkOrders.CheckMachine（维修工作单能力校验，ER3-WRK-01 已验证）",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_building_repairbay",
                SaveCompatible = true,
                LockedHintText = "开局已存在，有电即 Operational。",
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [SignalTowerId] = new MechanicalContentDef
            {
                Id = SignalTowerId,
                Category = MechanicalContentCategory.Building,
                DisplayName = "信号塔",
                Description = "扩大带宽，Operational 后开放破碎都市远征。",
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "开局 Damaged，修复40废料/15秒（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 40,
                Load = 0,
                ValuesSummary = "额外带宽5，开放破碎都市（DEMO-CONTENT-LOCK.md §2.1）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_signal_tower",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyPowerGrid.SignalBandwidth — 已在 ER3-PWR-01 用真实 Play Mode 验收",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_building_signal_tower",
                SaveCompatible = true,
                LockedHintText = "开局已存在（Damaged），修复后 Operational。",
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [BeaconId] = new MechanicalContentDef
            {
                Id = BeaconId,
                Category = MechanicalContentCategory.Building,
                DisplayName = "导航信标",
                Description = "战役终局建筑：Powered 后玩家用 E 确认启动，触发结算演出。",
                Source = MechanicalContentSource.UnlockedLateBuilding,
                SourceDetail = "解锁前不存在（DEMO-CONTENT-LOCK.md §2.2）；空间预留位已在 HomeValleyLayout.BeaconSlot 落定，真正建造由 ER7-BEACON-01 接手。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "Operational 电力需求30，默认优先级1（DEMO-CONTENT-LOCK.md §2.2）；OBJ-10 完成后 10 秒演出与一次性结算。",
                AiPermission = MechanicalContentAiPermission.PlayerOnly,
                IconId = "icon_building_beacon",
                ModelId = "primitive:cylinder",
                ActionId = "HomeValleyLayout.BeaconSlot（空间预留已实装）；建造/供电/E确认启动流程均未实现",
                VfxId = "vfx_beacon_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_building_beacon",
                SaveCompatible = true,
                LockedHintText = "需完成 OBJ-09（摧毁主核心并回收数据）后才能建造。",
                SilhouetteNote = "高耸信标塔剪影，全新内容，无 legacy 参照。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-18",
            },
        };

        public static IReadOnlyDictionary<string, MechanicalContentDef> All => _defs;

        public static bool TryGet(string id, out MechanicalContentDef def) => _defs.TryGetValue(id, out def);

        /// <summary>把 <see cref="BuildingRecord.BuildingTypeId"/> 实际取值（generator/generator_2/
        /// signal_tower 等 <see cref="HomeValleyLayout"/> 字符串常量）翻译成本类的建筑内容 ID。
        /// UI 展示建筑名字（如 <c>WorkOrderPanelUIToolkit</c> 的工单目标标签）必须经这个转换，
        /// 不能直接把内部 BuildingTypeId 拼接字符串显示给玩家。</summary>
        public static string ResolveByBuildingTypeId(string buildingTypeId)
        {
            switch (buildingTypeId)
            {
                case HomeValleyLayout.BuildingTypeCore:
                    return CoreId;
                case HomeValleyLayout.BuildingTypeGenerator:
                case HomeValleyLayout.BuildingTypeGenerator2:
                    return GeneratorId;
                case HomeValleyLayout.BuildingTypeWarehouse:
                    return WarehouseId;
                case HomeValleyLayout.BuildingTypeAssemblyStation:
                    return AssemblyStationId;
                case HomeValleyLayout.BuildingTypeAnalysisBench:
                    return AnalysisBenchId;
                case HomeValleyLayout.BuildingTypeRepairBay:
                    return RepairBayId;
                case HomeValleyLayout.BuildingTypeSignalTower:
                    return SignalTowerId;
                default:
                    return null;
            }
        }
    }
}
