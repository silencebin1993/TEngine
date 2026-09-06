using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// reaction-depth-and-combat-feel story-002：具名反应 id（<c>HitEvent.Payload["Reaction"]</c>
    /// 里的字符串，如 "Steam"）→ 玩家可读的中文播报文案。未收录的新反应直接回落显示原始 id 而不是
    /// 报错/隐藏——新增反应时忘了配文案只是不够好看，不该让表现层崩掉或假装什么都没发生。
    /// </summary>
    public static class ReactionFeedbackCatalog
    {
        private static readonly Dictionary<string, string> _labels = new Dictionary<string, string>
        {
            ["Steam"] = "蒸汽！",
            ["Deflagrate"] = "爆燃！",
            ["Conduct"] = "导电！",
            ["Shatter"] = "碎裂！",
            ["Sticky"] = "黏滞！",
            ["Electrolysis"] = "电解！",
            ["Insulate"] = "绝缘！",
            ["ThermalShock"] = "热震！",
            ["CausticBurn"] = "苛性灼烧！",
            ["Hemolysis"] = "溶血！",
            ["Syrup"] = "糖浆！",
            ["Dissolve"] = "溶解！",
            ["Annihilate"] = "湮灭！",
            ["Vitrify"] = "玻化！",
            ["Mudify"] = "泥泞！",
            ["Sepsis"] = "败血！",
        };

        public static string GetLabel(string reactionId)
        {
            if (string.IsNullOrEmpty(reactionId))
            {
                return "";
            }
            return _labels.TryGetValue(reactionId, out string label) ? label : reactionId;
        }

        // reaction-depth-and-combat-feel story-004：图鉴"已发现反应"页用的完整说明句（比战斗飘字
        // 用的 GetLabel 详细）。文案与 ComposeEngine ReactionCatalog.cs 里对应 ReactionRule 的
        // Description 参数同源手抄——两处保持一致是人工纪律，不是自动同步（ComposeEngine 核心刻意
        // 零 GameLogic 依赖，不能反过来读 GameLogic 的 Catalog）。
        private static readonly Dictionary<string, string> _descriptions = new Dictionary<string, string>
        {
            ["Steam"] = "火 + 水/湿 -> 蒸汽：清空热与湿，伤害小幅提升。",
            ["Deflagrate"] = "火 + 油 -> 爆燃：油层助燃，伤害大幅提升。",
            ["Conduct"] = "电 + 湿 -> 导电：感电与伤害都提升。",
            ["Shatter"] = "冻 + 物理命中 -> 碎裂：追加破甲式伤害。",
            ["Sticky"] = "酸 + 糖膜 -> 粘滞：目标黏住减速。",
            ["Electrolysis"] = "电 + 酸 -> 电解：酸作电解质强化导电，感电与伤害提升。",
            ["Insulate"] = "电 + 油 -> 绝缘：油层阻断导电，唯一的克制类反应，伤害不增反减。",
            ["ThermalShock"] = "火 + 冻 -> 热震：骤热骤冷致其碎裂，伤害提升。",
            ["CausticBurn"] = "火 + 酸 -> 苛性灼烧：酸性介质助燃，燃烧强度叠加。",
            ["Hemolysis"] = "血 + 酸 -> 溶血：细胞膜瞬间溶解，伤害提升。",
            ["Syrup"] = "湿 + 糖膜 -> 糖浆：黏稠介质裹住目标，伤害小幅提升。",
            ["Dissolve"] = "酸 + 油 -> 溶解：油脂被乳化分解，伤害提升。",
            ["Annihilate"] = "光 + 暗 -> 湮灭：两极相消爆发，伤害大幅提升（全部反应里倍率最高）。",
            ["Vitrify"] = "土 + 火 -> 玻化：高温烧结成脆玻璃，伤害提升。",
            ["Mudify"] = "土 + 湿 -> 泥泞：能量被泥浆吸收，第二条克制类反应，伤害不增反减。",
            ["Sepsis"] = "毒 + 血 -> 败血：毒素随血流扩散全身，伤害提升。",
        };

        /// <summary>全部已知反应短名（与 <see cref="GetLabel"/>/<see cref="GetDescription"/> 同一 key
        /// 空间），供图鉴"已发现反应"页遍历全量目录用。</summary>
        public static IEnumerable<string> AllReactionIds => _descriptions.Keys;

        public static string GetDescription(string reactionId)
        {
            if (string.IsNullOrEmpty(reactionId))
            {
                return "";
            }
            return _descriptions.TryGetValue(reactionId, out string desc) ? desc : reactionId;
        }
    }
}
