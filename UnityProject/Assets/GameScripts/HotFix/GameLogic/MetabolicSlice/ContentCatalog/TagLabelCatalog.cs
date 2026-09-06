using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// reaction-depth-and-combat-feel story-004：Tag 字符串（<c>TagAttach</c> 打的那个值，如 "Fire"）
    /// → 玩家可读的中文名。未收录的 tag 直接回落原始字符串（不报错、不隐藏），同
    /// <see cref="ReactionFeedbackCatalog"/> 的兜底哲学一致。
    /// </summary>
    public static class TagLabelCatalog
    {
        private static readonly Dictionary<string, string> _labels = new Dictionary<string, string>
        {
            // Substance（会参与 Phase 1/5 命名反应）
            ["Fire"] = "火",
            ["Wet"] = "潮湿",
            ["Shock"] = "雷",
            ["Oil"] = "油",
            ["SugarFilm"] = "糖膜",
            ["Acid"] = "酸",
            ["Frozen"] = "冻",
            ["Blood"] = "血",
            ["Slow"] = "迟缓",
            ["Charged"] = "充能",
            ["Earth"] = "土",
            ["Light"] = "光",
            ["Dark"] = "暗",
            ["Poison"] = "毒",
            ["Haste"] = "迅捷",
            // 纯内容/规则标签（不参与 Substance 混合，仅供其它系统识别来源）
            ["Mirror"] = "镜面",
            ["ReceptorMemory"] = "记忆",
            ["Apoptosis"] = "凋亡",
            ["WallImpact"] = "撞墙",
            ["Magnet"] = "磁力",
            ["Drifting"] = "飘移",
            ["Capillary"] = "毛细",
            ["InheritPattern"] = "传承",
        };

        public static string GetLabel(string tag) =>
            string.IsNullOrEmpty(tag) ? "" : (_labels.TryGetValue(tag, out string label) ? label : tag);
    }
}
