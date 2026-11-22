using System;
using System.Collections.Generic;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：3 底盘（DEMO-CONTENT-LOCK.md §2.4"槽位：底盘"、§3"底盘"行）。
    /// 与 <see cref="Regions.HomeValleyLayout"/> 已经存在的 erc_001/002/003 具体机型区分开——那三个是
    /// "具体家园开局机的 <see cref="MachineRecord.ChassisId"/> 取值"（每台都自带默认装配），本类是
    /// "底盘这一装配槽位本身"的机械内容定义（HP/带宽/货位/容量），erc_001/002 都属于
    /// <see cref="ChassisWheelId"/>，erc_003 属于 <see cref="ChassisTrackId"/>——见
    /// <see cref="ResolveArchetype"/>。</summary>
    public static class ChassisCatalog
    {
        public const string ChassisWheelId = "chassis_wheel";
        public const string ChassisTrackId = "chassis_track";
        public const string ChassisHoverId = "chassis_hover";
        /// <summary>FG2-FW-02（FGR-FW-012、FG06 FGR-DEF-001）：炮塔的固定底盘。兼容表（冲刺器装不上、推铲读作击退铲）从本 Story 起生效；
        /// FG6-DEF-01 起炮塔开放（放置 / 施工 / 目标模式 / 补给 = <see cref="Defense.TurretService"/>），固定底盘进基础蓝图库（来源 BaseBlueprint，不再要解锁记录）。</summary>
        public const string ChassisFixedId = "chassis_fixed";

        /// <summary>FG15-UX-03：Demo 条目的文案走文本键，每次按当前语言重建（静态初始化时文本表可能还没载入）。</summary>
        private static Dictionary<string, MechanicalContentDef> DemoDefs() => new Dictionary<string, MechanicalContentDef>
        {
            [ChassisWheelId] = new MechanicalContentDef
            {
                Id = ChassisWheelId,
                Category = MechanicalContentCategory.Chassis,
                DisplayName = GameText.Get("chassis.demo.hauler_wheels"),
                Description = GameText.Get("chassis.demo.wheeled_chassis_fast_with"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-001/ERC-002 开局即以本底盘登记（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "底盘",
                ScrapCost = 15,
                Load = 4, // 容量（负载上限）
                ValuesSummary = GameText.Get("chassis.demo.hp_100_bandwidth_1"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_chassis_wheel",
                ModelId = "primitive:capsule",
                ActionId = "MachineRegistry.SpawnMachine(chassisId: HomeValleyLayout.Erc001ChassisId/Erc002ChassisId) — HomeValleyController.cs:532",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_move_wheel",
                PreviewId = "preview_chassis_wheel",
                SaveCompatible = true,
                LockedHintText = GameText.Get("chassis.demo.already_in_the_base"),
                SilhouetteNote = "轮式：矮宽剪影，四角有轮廓凸起（占位阶段与其它底盘共用胶囊体，无法靠轮廓区分，登记 DEBT）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-14",
            },
            [ChassisTrackId] = new MechanicalContentDef
            {
                Id = ChassisTrackId,
                Category = MechanicalContentCategory.Chassis,
                DisplayName = GameText.Get("chassis.demo.combat_tracks"),
                Description = GameText.Get("chassis.demo.tracked_chassis_with_the"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；由装配站生产（DEMO-CONTENT-LOCK.md 第53行），归还谷地本身不出生这台机器。",
                Slot = "底盘",
                ScrapCost = 20,
                Load = 7,
                ValuesSummary = GameText.Get("chassis.demo.hp_140_bandwidth_2"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_chassis_track",
                ModelId = "primitive:capsule",
                ActionId = "MachineRegistry.SpawnMachine(chassisId: HomeValleyLayout.Erc003ChassisId) — 生产队列待 ER4-FAC-01",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_move_track",
                PreviewId = "preview_chassis_track",
                SaveCompatible = true,
                LockedHintText = GameText.Get("chassis.demo.already_in_the_base_2"),
                SilhouetteNote = "履带：更高更重的剪影（占位阶段几何体与轮式相同，登记 DEBT）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-02",
            },
            [ChassisHoverId] = new MechanicalContentDef
            {
                Id = ChassisHoverId,
                Category = MechanicalContentCategory.Chassis,
                DisplayName = GameText.Get("chassis.demo.hover_repair"),
                Description = GameText.Get("chassis.demo.hover_chassis_with_a"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；解析台（AnalysisBench）转为 Operational 后可在装配站生产（DEMO-CONTENT-LOCK.md §3）。",
                Slot = "底盘",
                ScrapCost = 30,
                Load = 6,
                ValuesSummary = GameText.Get("chassis.demo.hp_80_bandwidth_1"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_chassis_hover",
                ModelId = "primitive:capsule",
                ActionId = "MachineRegistry.SpawnMachine(chassisId: ChassisCatalog.ChassisHoverId) — 已用 execute_code 真实调用验证登记/HP/货位；生产队列待 ER4-FAC-01",
                VfxId = "vfx_hover_placeholder",
                SfxId = "sfx_move_hover",
                PreviewId = "preview_chassis_hover",
                SaveCompatible = true,
                LockedHintText = GameText.Get("chassis.demo.repair_and_power_the"),
                SilhouetteNote = "悬浮：离地间隙+外倾裙板剪影（占位阶段几何体与其它底盘相同，登记 DEBT）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-02",
            },
        };

        private static Dictionary<string, MechanicalContentDef> _merged;
        private static GameLanguage _mergedLanguage;
        private static int _mergedTextRevision = -1;
        private static int _buildCount;

        /// <summary>Demo 的 3 个底盘 + 固定底盘（名称走文本键，按语言缓存）。</summary>
        public static IReadOnlyDictionary<string, MechanicalContentDef> All
        {
            get
            {
                EnsureBuilt();
                return _merged;
            }
        }

        public static int Revision
        {
            get
            {
                EnsureBuilt();
                return _buildCount;
            }
        }

        public static bool TryGet(string id, out MechanicalContentDef def)
        {
            if (string.IsNullOrEmpty(id))
            {
                def = null;
                return false;
            }
            EnsureBuilt();
            return _merged.TryGetValue(id, out def);
        }

        private static void EnsureBuilt()
        {
            GameLanguage lang = GameText.Language;
            int textRev = GameText.Revision;
            if (_merged != null && _mergedLanguage == lang && _mergedTextRevision == textRev)
            {
                return;
            }
            var merged = new Dictionary<string, MechanicalContentDef>(DemoDefs(), StringComparer.Ordinal)
            {
                [ChassisFixedId] = new MechanicalContentDef
                {
                    Id = ChassisFixedId,
                    Category = MechanicalContentCategory.Chassis,
                    DisplayName = GameText.Get("chassis.fixed.name"),
                    Description = GameText.Get("chassis.fixed.desc"),
                    // FG6-DEF-01：炮塔开放——固定底盘进基础蓝图库（之前是 TurretProgram，要解锁记录）。
                    Source = MechanicalContentSource.BaseBlueprint,
                    SourceDetail = GameText.Get("chassis.fixed.locked"),
                    Slot = "底盘",
                    ScrapCost = 0,
                    Load = 6,
                    ValuesSummary = GameText.Get("chassis.fixed.desc"),
                    AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                    IconId = "icon_chassis_fixed",
                    ModelId = "primitive:capsule",
                    ActionId = "CarrierReadings.CanMount（固定底盘兼容表 fg.TbCombatComponent.turret）；炮塔放置 / 施工 / 目标模式 / 补给 = Defense.TurretService（FG6-DEF-01）",
                    VfxId = "vfx_none_placeholder",
                    SfxId = "sfx_move_track", // 占位：炮塔转向音（美术 / 音频阶段整体替换，DEBT-FG6DEF01-01）
                    PreviewId = "preview_chassis_fixed",
                    SaveCompatible = true,
                    LockedHintText = GameText.Get("chassis.fixed.locked"),
                    SilhouetteNote = "炮塔基座（占位：炮塔座方块 + 炮口，美术阶段换正式模型，DEBT-FG6DEF01-01）。",
                    LegacyFacadeId = null,
                    DebtId = "DEBT-FG6DEF01-01",
                },
            };
            _merged = merged;
            _mergedLanguage = lang;
            _mergedTextRevision = textRev;
            _buildCount++;
        }

        /// <summary>把 <see cref="MachineRecord.ChassisId"/> 实际取值（erc_001/002/003/chassis_hover 等）
        /// 翻译成本类的底盘内容 ID——两套 ID 语义不同（见类注释），UI/图鉴显示底盘信息时必须经这个
        /// 转换，不能直接拿 MachineRecord.ChassisId 当 key 查本表（erc_001/002 都会 miss）。</summary>
        public static string ResolveArchetype(string machineChassisId)
        {
            switch (machineChassisId)
            {
                case HomeValleyLayout.Erc001ChassisId:
                case HomeValleyLayout.Erc002ChassisId:
                    return ChassisWheelId;
                case HomeValleyLayout.Erc003ChassisId:
                    return ChassisTrackId;
                case ChassisHoverId:
                    return ChassisHoverId;
                case ChassisFixedId:
                    return ChassisFixedId;
                default:
                    return null; // erc_rescue 等系统占位机不是玩家可见内容目录条目，返回 null 由调用方决定回退文案。
            }
        }
    }
}
