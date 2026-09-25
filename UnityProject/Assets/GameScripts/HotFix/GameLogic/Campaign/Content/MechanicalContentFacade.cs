using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：封闭内容目录的聚合查询入口。STORY-EXECUTION-CARDS.md 第1条要求"建立
    /// 封闭目录"，本类把 <see cref="ChassisCatalog"/>/<see cref="ComponentCatalog"/>/
    /// <see cref="FirmwareCatalog"/>/<see cref="MechanicalReactionCatalog"/>/<see cref="EnemyCatalog"/>/
    /// <see cref="BuildingCatalog"/> 六张表合成一份可整体审计的集合（3+4+2+4+6+2+6+8=35 条），
    /// 并提供真实生产代码（<c>WorkOrderPanelUIToolkit</c> 等）可以直接调用的展示名解析。</summary>
    public static class MechanicalContentFacade
    {
        /// <summary>全部 35 条内容目录条目，按 Id 唯一索引。静态构造期一次性合并，若任意两张子表出现
        /// 重复 Id 会抛异常——这是本 Story 明确要求的"封闭目录"完整性自检，不应该被静默吞掉。</summary>
        public static readonly IReadOnlyDictionary<string, MechanicalContentDef> All = BuildAll();

        private static Dictionary<string, MechanicalContentDef> BuildAll()
        {
            var merged = new Dictionary<string, MechanicalContentDef>();
            void Merge(IReadOnlyDictionary<string, MechanicalContentDef> src)
            {
                foreach (KeyValuePair<string, MechanicalContentDef> kv in src)
                {
                    if (merged.ContainsKey(kv.Key))
                    {
                        throw new InvalidOperationException(
                            $"MechanicalContentFacade：内容 ID 冲突 '{kv.Key}'，封闭目录要求全局唯一。");
                    }
                    merged[kv.Key] = kv.Value;
                }
            }

            Merge(ChassisCatalog.All);
            Merge(ComponentCatalog.All);
            Merge(FirmwareCatalog.All);
            Merge(MechanicalReactionCatalog.All);
            Merge(EnemyCatalog.All);
            Merge(BuildingCatalog.All);
            return merged;
        }

        public static bool TryGet(string id, out MechanicalContentDef def) => All.TryGetValue(id, out def);

        /// <summary>供审计用：按类别统计条数，供 execute_code 一次性断言 3/4/2/4/6/2/6/8 齐全。</summary>
        public static int CountByCategory(MechanicalContentCategory category) =>
            All.Values.Count(d => d.Category == category);

        /// <summary>工单目标标签展示名解析——<see cref="Regions.WorkOrderRecord.TargetId"/> 对建筑类目标
        /// 的取值格式是 <c>"home_valley:generator"</c>（<see cref="HomeValleyLayout.RegionId"/> + ":" +
        /// BuildingTypeId，见 <c>HomeValleyWorkOrders.TryCreateRepair</c>）。剥离区域前缀后查
        /// fg.TbBuilding 的名称文本键（<see cref="FgContentTables"/> + <see cref="Localization.GameText"/>）；
        /// 查不到（地面物/残骸等非建筑目标）安全回退成原始 id，不抛异常、不显示空白。</summary>
        public static string ResolveWorkOrderTargetLabel(string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return targetId;
            }

            string key = targetId;
            string prefix = HomeValleyLayout.RegionId + ":";
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                key = key.Substring(prefix.Length);
            }

            // FG0-DATA-01（FGR-ARC-006）：建筑名走文本键——fg.TbBuilding 的 nameKey → GameText（当前语言）。
            // 查不到建筑行（地面物/残骸等非建筑目标）仍按原约定返回原始 id，由调用方给通用称呼；
            // 查到行但文本键缺失时返回 ⟦key⟧，让缺失在界面上可见，而不是悄悄退回内部 id。
            // 顺带修正：导航信标此前不在 BuildingCatalog.ResolveByBuildingTypeId 里，工单/字幕只能显示通用称呼。
            if (FgContentTables.TryGetBuilding(key, out GameConfig.fg.Building row))
            {
                return Localization.GameText.Get(row.NameKey);
            }

            return targetId;
        }

        /// <summary>机器底盘展示名解析——同上，剥离 <see cref="MachineRecord.ChassisId"/>（erc_001 等）
        /// 到底盘内容目录 DisplayName 的一步查表，供未来图鉴/工单面板复用，查不到回退原始 id。</summary>
        public static string ResolveChassisLabel(string machineChassisId)
        {
            string archetype = ChassisCatalog.ResolveArchetype(machineChassisId);
            if (archetype != null && ChassisCatalog.TryGet(archetype, out MechanicalContentDef def))
            {
                return def.DisplayName;
            }
            return machineChassisId;
        }
    }
}
