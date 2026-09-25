using System.Collections.Generic;
using GameLogic.Campaign.Regions;

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

        private static readonly Dictionary<string, MechanicalContentDef> _defs = new Dictionary<string, MechanicalContentDef>
        {
            [ChassisWheelId] = new MechanicalContentDef
            {
                Id = ChassisWheelId,
                Category = MechanicalContentCategory.Chassis,
                DisplayName = "搬运轮式",
                Description = "轮式底盘，速度快、货位多，适合搬运与建造类工作单。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-001/ERC-002 开局即以本底盘登记（DEMO-CONTENT-LOCK.md §2.1）。",
                Slot = "底盘",
                ScrapCost = 15,
                Load = 4, // 容量（负载上限）
                ValuesSummary = "HP100，带宽1，基础携带2货位（ERC-001 另带货舱结构达4货位）；可搬运/建造/拆解/建筑基础维修/编队/直控。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_chassis_wheel",
                ModelId = "primitive:capsule",
                ActionId = "MachineRegistry.SpawnMachine(chassisId: HomeValleyLayout.Erc001ChassisId/Erc002ChassisId) — HomeValleyController.cs:532",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_move_wheel",
                PreviewId = "preview_chassis_wheel",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "轮式：矮宽剪影，四角有轮廓凸起（占位阶段与其它底盘共用胶囊体，无法靠轮廓区分，登记 DEBT）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-14",
            },
            [ChassisTrackId] = new MechanicalContentDef
            {
                Id = ChassisTrackId,
                Category = MechanicalContentCategory.Chassis,
                DisplayName = "战斗履带",
                Description = "履带底盘，HP 与负载最高，是默认战斗机型 ERC-003 的底盘。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；由装配站生产（DEMO-CONTENT-LOCK.md 第53行），归还谷地本身不出生这台机器。",
                Slot = "底盘",
                ScrapCost = 20,
                Load = 7,
                ValuesSummary = "HP140，带宽2，基础携带1货位；默认 ERC-003 初装连射器、冲刺器、寻的、拖尾。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_chassis_track",
                ModelId = "primitive:capsule",
                ActionId = "MachineRegistry.SpawnMachine(chassisId: HomeValleyLayout.Erc003ChassisId) — 生产队列待 ER4-FAC-01",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_move_track",
                PreviewId = "preview_chassis_track",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁；需装配站产出才能实际获得一台。",
                SilhouetteNote = "履带：更高更重的剪影（占位阶段几何体与轮式相同，登记 DEBT）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-02",
            },
            [ChassisHoverId] = new MechanicalContentDef
            {
                Id = ChassisHoverId,
                Category = MechanicalContentCategory.Chassis,
                DisplayName = "维修悬浮",
                Description = "悬浮底盘，初装维修束，专精战场/建筑维修工作。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；解析台（AnalysisBench）转为 Operational 后可在装配站生产（DEMO-CONTENT-LOCK.md §3）。",
                Slot = "底盘",
                ScrapCost = 30,
                Load = 6,
                ValuesSummary = "HP80，带宽1，基础携带1货位；初装维修束+两个空固件槽；可修机器，不能替代修复建筑的基础工作权限校验。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_chassis_hover",
                ModelId = "primitive:capsule",
                ActionId = "MachineRegistry.SpawnMachine(chassisId: ChassisCatalog.ChassisHoverId) — 已用 execute_code 真实调用验证登记/HP/货位；生产队列待 ER4-FAC-01",
                VfxId = "vfx_hover_placeholder",
                SfxId = "sfx_move_hover",
                PreviewId = "preview_chassis_hover",
                SaveCompatible = true,
                LockedHintText = "需先修复归还谷地解析台（AnalysisBench）通电，才能在装配站生产。",
                SilhouetteNote = "悬浮：离地间隙+外倾裙板剪影（占位阶段几何体与其它底盘相同，登记 DEBT）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-02",
            },
        };

        public static IReadOnlyDictionary<string, MechanicalContentDef> All => _defs;

        public static bool TryGet(string id, out MechanicalContentDef def) => _defs.TryGetValue(id, out def);

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
                default:
                    return null; // erc_rescue 等系统占位机不是玩家可见内容目录条目，返回 null 由调用方决定回退文案。
            }
        }
    }
}
