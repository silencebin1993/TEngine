using System.Collections.Generic;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;

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

        private static Dictionary<string, MechanicalContentDef> _defs;
        private static GameLanguage _defsLanguage;
        private static int _defsTextRevision = -1;

        /// <summary>FG15-UX-03：按当前语言缓存的条目（切语言 / 重载文本表后下一次读取重建）。</summary>
        private static Dictionary<string, MechanicalContentDef> Defs
        {
            get
            {
                if (_defs == null || _defsLanguage != GameText.Language || _defsTextRevision != GameText.Revision)
                {
                    _defs = DemoDefs();
                    _defsLanguage = GameText.Language;
                    _defsTextRevision = GameText.Revision;
                }
                return _defs;
            }
        }

        /// <summary>FG15-UX-03：Demo 条目的文案走文本键，每次按当前语言重建（静态初始化时文本表可能还没载入）。</summary>
        private static Dictionary<string, MechanicalContentDef> DemoDefs() => new Dictionary<string, MechanicalContentDef>
        {
            [CoreId] = new MechanicalContentDef
            {
                Id = CoreId,
                Category = MechanicalContentCategory.Building,
                DisplayName = GameText.Get("building.core.name"),
                Description = GameText.Get("building.demo.the_campaign_s_one"),
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "开局已存在，Operational，不可修复材料前置、不可关停（DEMO-CONTENT-LOCK.md §2.1/§2.2）。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = GameText.Get("building.demo.hp_500_base_power"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_core",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyLayout.BaseCoreSupply/CoreCacheCapacity + HomeValleySoftlockGuard（核心被毁失败界面，ER3-SOFTLOCK-01 已验证）",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_build_complete",
                PreviewId = "preview_building_core",
                SaveCompatible = true,
                LockedHintText = GameText.Get("building.demo.present_from_the_start"),
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分（红/黄/橙/绿/灰），类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [GeneratorId] = new MechanicalContentDef
            {
                Id = GeneratorId,
                Category = MechanicalContentCategory.Building,
                DisplayName = GameText.Get("building.generator.name"),
                Description = GameText.Get("building.demo.power_building_once_operational"),
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "开局 Damaged，修复30废料/20秒；可另建第二座（60废料/40秒），两者是同一内容的两个实例（DEMO-CONTENT-LOCK.md §2.1/§2.2）。",
                Slot = "建筑",
                ScrapCost = 30,
                Load = 0,
                ValuesSummary = GameText.Get("building.demo.80_power_each_once"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_generator",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyWorkOrders.TryCreateRepair/TryCreateBuild + HomeValleyPowerGrid.Recompute — 已在 ER3-PWR-01/WRK-01 用真实 Play Mode 验收",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_power_up",
                PreviewId = "preview_building_generator",
                SaveCompatible = true,
                LockedHintText = GameText.Get("building.demo.present_from_the_start_2"),
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [WarehouseId] = new MechanicalContentDef
            {
                Id = WarehouseId,
                Category = MechanicalContentCategory.Building,
                DisplayName = GameText.Get("building.warehouse.name"),
                Description = GameText.Get("building.demo.adds_storage_beyond_the"),
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "开局 Damaged，修复10废料/10秒（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 10,
                Load = 0,
                ValuesSummary = GameText.Get("building.demo.capacity_300_once_operational"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_warehouse",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyCargo.GetStorageCapacity — 已在 ER3-STO-01 用真实存读档/仓满场景验证",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_build_complete",
                PreviewId = "preview_building_warehouse",
                SaveCompatible = true,
                LockedHintText = GameText.Get("building.demo.present_from_the_start_2"),
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [AssemblyStationId] = new MechanicalContentDef
            {
                Id = AssemblyStationId,
                Category = MechanicalContentCategory.Building,
                DisplayName = GameText.Get("building.assembly_station.name"),
                Description = GameText.Get("building.demo.produces_and_refits_machines"),
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "建筑已存在；有足额电力即 Operational，无额外修复材料前置（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = GameText.Get("building.demo.operational_power_demand_25"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_assembly",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyPowerGrid.Recompute（供电已实装）；生产队列 UI/流程见 ER4-FAC-01",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_production_complete",
                PreviewId = "preview_building_assembly",
                SaveCompatible = true,
                LockedHintText = GameText.Get("building.demo.present_from_the_start_3"),
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-02",
            },
            [AnalysisBenchId] = new MechanicalContentDef
            {
                Id = AnalysisBenchId,
                Category = MechanicalContentCategory.Building,
                DisplayName = GameText.Get("building.analysis_bench.name"),
                Description = GameText.Get("building.demo.analyzes_recovered_silent_or"),
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "建筑已存在；有足额电力即 Operational，无额外修复材料前置（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = GameText.Get("building.demo.operational_power_demand_15"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_analysis",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyPowerGrid.Recompute（供电已实装）；解析流程 UI 见 ER6-ANA-01",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_analysis_complete",
                PreviewId = "preview_building_analysis",
                SaveCompatible = true,
                LockedHintText = GameText.Get("building.demo.present_from_the_start_3"),
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-17",
            },
            [RepairBayId] = new MechanicalContentDef
            {
                Id = RepairBayId,
                Category = MechanicalContentCategory.Building,
                DisplayName = GameText.Get("building.repair_bay.name"),
                Description = GameText.Get("building.demo.base_for_hover_repair"),
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "建筑已存在；有足额电力即 Operational，无额外修复材料前置（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = GameText.Get("building.demo.operational_power_demand_15_2"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_repair",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyPowerGrid.Recompute + HomeValleyWorkOrders.CheckMachine（维修工作单能力校验，ER3-WRK-01 已验证）",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_build_complete",
                PreviewId = "preview_building_repairbay",
                SaveCompatible = true,
                LockedHintText = GameText.Get("building.demo.present_from_the_start_3"),
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [SignalTowerId] = new MechanicalContentDef
            {
                Id = SignalTowerId,
                Category = MechanicalContentCategory.Building,
                DisplayName = GameText.Get("building.signal_tower.name"),
                Description = GameText.Get("building.demo.expands_bandwidth_once_operation"),
                Source = MechanicalContentSource.AlwaysBuiltHomeValley,
                SourceDetail = "开局 Damaged，修复40废料/15秒（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "建筑",
                ScrapCost = 40,
                Load = 0,
                ValuesSummary = GameText.Get("building.demo.5_bandwidth_opens_fractured"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_building_signal_tower",
                ModelId = "primitive:cube",
                ActionId = "HomeValleyPowerGrid.SignalBandwidth — 已在 ER3-PWR-01 用真实 Play Mode 验收",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_signal_restore",
                PreviewId = "preview_building_signal_tower",
                SaveCompatible = true,
                LockedHintText = GameText.Get("building.demo.present_from_the_start_2"),
                SilhouetteNote = "已用真实 ColorForBuilding 状态色区分，类型剪影待美术。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [BeaconId] = new MechanicalContentDef
            {
                Id = BeaconId,
                Category = MechanicalContentCategory.Building,
                DisplayName = GameText.Get("building.beacon.name"),
                Description = GameText.Get("building.demo.the_campaign_s_final"),
                Source = MechanicalContentSource.UnlockedLateBuilding,
                SourceDetail = "解锁前不存在（DEMO-CONTENT-LOCK.md §2.2）；空间预留位已在 HomeValleyLayout.BeaconSlot 落定，真正建造由 ER7-BEACON-01 接手。",
                Slot = "建筑",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = GameText.Get("building.demo.operational_power_demand_30"),
                AiPermission = MechanicalContentAiPermission.PlayerOnly,
                IconId = "icon_building_beacon",
                ModelId = "primitive:cylinder",
                ActionId = "HomeValleyLayout.BeaconSlot（空间预留已实装）；建造/供电/E确认启动流程均未实现",
                VfxId = "vfx_beacon_placeholder",
                SfxId = "sfx_beacon_launch",
                PreviewId = "preview_building_beacon",
                SaveCompatible = true,
                LockedHintText = GameText.Get("building.demo.can_only_be_built"),
                SilhouetteNote = "高耸信标塔剪影，全新内容，无 legacy 参照。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-18",
            },
        };

        public static IReadOnlyDictionary<string, MechanicalContentDef> All => Defs;

        public static bool TryGet(string id, out MechanicalContentDef def) => Defs.TryGetValue(id, out def);

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
