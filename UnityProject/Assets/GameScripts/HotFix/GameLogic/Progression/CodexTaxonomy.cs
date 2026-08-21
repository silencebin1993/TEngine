using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.Progression
{
    /// <summary>
    /// 图鉴“插槽/地形/状态”三类的中文名+一句话说明（story-005 D4）。这三类底层只是代码枚举/
    /// 无描述字段的小目录（<see cref="SlotType"/>/<see cref="SimStatus"/>/
    /// <c>GameLogic.MetabolicSlice.ContentCatalog.TerrainCatalog</c>），量小，按 001 R4 决策
    /// 由 005 UI 层直接内联一份 Dictionary，不新建 Catalog 类、不改数据层（002 已收口）。
    /// </summary>
    internal static class CodexTaxonomy
    {
        private static readonly Dictionary<SlotType, (string Name, string Description)> SlotDefs = new()
        {
            [SlotType.Cytoplasm] = ("胞质", "基础插槽，微导热。"),
            [SlotType.Membrane] = ("膜缘", "易湿、受击减伤。"),
            [SlotType.Lattice] = ("晶格", "传导损耗降低。"),
            [SlotType.Perinuclear] = ("核周", "基因效力增强（效果钩子尚未接入）。"),
            [SlotType.Secretory] = ("分泌", "输出延迟增加。"),
            [SlotType.AcidFen] = ("酸沼", "打酸蚀标记，并微加热。"),
        };

        public static string SlotTypeName(SlotType slot) =>
            SlotDefs.TryGetValue(slot, out var d) ? d.Name : slot.ToString();

        public static string SlotTypeDescription(SlotType slot) =>
            SlotDefs.TryGetValue(slot, out var d) ? d.Description : string.Empty;

        private static readonly Dictionary<string, (string Name, string Description)> TerrainDefs = new()
        {
            ["ter_floor"] = ("平地", "无固有标记的基础地板。"),
            ["ter_wet"] = ("湿地", "踩上后打湿润标记，配合电/冰类效果加深。"),
            ["ter_oil"] = ("油污", "踩上后打油污标记，配合火焰类效果易燃。"),
            ["ter_acid"] = ("酸沼", "踩上后打酸蚀标记，持续腐蚀。"),
            ["ter_sugar"] = ("糖膜", "踩上后打糖膜标记，配合特定联动效果。"),
            ["ter_light"] = ("光照", "踩上后打光照标记。"),
            ["ter_shadow"] = ("阴影", "踩上后打阴影标记。"),
            ["ter_salt"] = ("盐霜", "踩上后打盐霜标记。"),
        };

        public static string TerrainName(string terrainId) =>
            TerrainDefs.TryGetValue(terrainId, out var d) ? d.Name : terrainId;

        public static string TerrainDescription(string terrainId) =>
            TerrainDefs.TryGetValue(terrainId, out var d) ? d.Description : string.Empty;

        private static readonly Dictionary<SimStatus, string> StatusDescriptions = new()
        {
            [SimStatus.Conductive] = "导电：可被电弧连锁命中。",
            [SimStatus.Breached] = "破体：被吞噬的体积门槛下降。",
            [SimStatus.Marked] = "标记：受到额外伤害，可被追踪效果锁定。",
            [SimStatus.Slowed] = "减速：移动速度下降。",
            [SimStatus.Stunned] = "麻痹：无法移动。",
            [SimStatus.Corroded] = "腐蚀：持续掉血且体积下降。",
            [SimStatus.Feared] = "恐惧：转为逃跑行为。",
            [SimStatus.Parasited] = "寄生：持续被抽取资源。",
            [SimStatus.Invulnerable] = "无敌：免疫伤害。",
            [SimStatus.Hardened] = "硬化：受击阈值提高。",
            [SimStatus.Crystallized] = "晶化：累积破碎层数。",
            [SimStatus.Infected] = "感染：死亡时生成友方单位。",
            [SimStatus.Vulnerable] = "易伤：受到伤害提高。",
            [SimStatus.Burning] = "燃烧：持续受到高温伤害。",
            [SimStatus.Polluted] = "污染：受污染相关效果影响。",
            [SimStatus.Pulled] = "被引力拉拽。",
            [SimStatus.Unedible] = "不可被吞噬（护壳完整）。",
            [SimStatus.Elite] = "精英单位标记，用于视觉与掉落区分。",
            [SimStatus.Boss] = "首领单位标记。",
            [SimStatus.OnMycelium] = "处于菌毯区域内。",
            [SimStatus.Overloaded] = "过载：反应矩阵持久状态。",
            [SimStatus.Telegraphing] = "前摇中：蓄力预警。",
        };

        public static string StatusName(SimStatus status) => status.ToString();

        public static string StatusDescription(SimStatus status) =>
            StatusDescriptions.TryGetValue(status, out var d) ? d : string.Empty;
    }
}
