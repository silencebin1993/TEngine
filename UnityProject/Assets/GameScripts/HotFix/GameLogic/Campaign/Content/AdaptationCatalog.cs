using System.Collections.Generic;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER6-ADAPT-01 STORY-EXECUTION-CARDS.md：敌方出发时锁定反制的封闭枚举 + 玩家可读情报文案
    /// （DisplayName/来源/危险/应对建议——验收卡第2条"准备页写反制名、信息来源、具体危险和玩家可用
    /// 应对建议"）。<see cref="Regions.RegionRecord.AdaptationId"/> 唯一写入口是
    /// <see cref="Regions.EnemyAdaptationService"/>，本类只是内容目录 + 文案，不做选择判定。
    ///
    /// DEMO-CONTENT-LOCK.md §5"适应 HeatResistant/Flanker/JammerSupport 三者一次只选一个；分别加
    /// 耐热装甲、一个侧袭出生组、一个干扰支援组；不改玩家构筑、不临时切换"——四个 id（含 None）与
    /// 该文档字面一致，不额外发明第五种。</summary>
    public static class AdaptationCatalog
    {
        public const string None = "adaptation_none";
        public const string HeatResistant = "adaptation_heat_resistant";
        public const string Flanker = "adaptation_flanker";
        public const string JammerSupport = "adaptation_jammer_support";

        public readonly struct AdaptationInfo
        {
            public readonly string Id;
            public readonly string DisplayName;
            /// <summary>信息来源——验收卡第2条点名字段，解释玩家为什么会看到这条情报。</summary>
            public readonly string SourceText;
            /// <summary>具体危险——这次适应在战斗中会造成什么真实变化。</summary>
            public readonly string HazardText;
            /// <summary>玩家可用应对建议——不是纯警告，给出可执行的对策。</summary>
            public readonly string CounterHintText;

            public AdaptationInfo(string id, string displayName, string sourceText, string hazardText, string counterHintText)
            {
                Id = id;
                DisplayName = displayName;
                SourceText = sourceText;
                HazardText = hazardText;
                CounterHintText = counterHintText;
            }
        }

        private static readonly Dictionary<string, AdaptationInfo> Infos = new Dictionary<string, AdaptationInfo>
        {
            [None] = new AdaptationInfo(
                None,
                "无反制（None）",
                "上次远征信号暴露未突破60阈值，敌方尚未截获足够情报。",
                "安全默认：敌方沿用既有编制，本次不会有针对性调整。",
                "无需特别应对，按常规装配出击即可。"),
            [HeatResistant] = new AdaptationInfo(
                HeatResistant,
                "耐热适应",
                "上次远征信号暴露突破60阈值：铸造分部截获我方熔穿过载战术情报。",
                "护甲机正面加装耐热衬层，熔穿过载额外穿甲的收益被完全抵消（伤害回落到未过载时的正面减伤基线，基础重炮伤害不受影响）。",
                "改走侧后攻击角度绕开正面装甲，或换标记跳转/常规连射打法，不要单靠过载穿甲强攻正面。"),
            [Flanker] = new AdaptationInfo(
                Flanker,
                "侧袭编队",
                "上次远征信号暴露突破60阈值：截获我方分散点杀战术情报。",
                "入场后额外一组敌人从侧翼伏击布防，专打脱离编队、孤立站位的机器。",
                "进场后保持编队集中推进，先侦察侧翼动向再考虑分兵。"),
            [JammerSupport] = new AdaptationInfo(
                JammerSupport,
                "干扰支援",
                "上次远征信号暴露突破60阈值：我方装配未见明显专精反应，铸造前哨调来通用压制力量。",
                "额外一台干扰机进驻支援位置，发现目标即主动开火，整体战场火力密度上升。",
                "优先集中火力清掉支援干扰机，或保持距离用远程装配逐个击破。"),
        };

        /// <summary>唯一读取入口——<paramref name="adaptationId"/> 为空/null（区域记录尚未出征锁定过、
        /// 或 <see cref="Regions.EnemyAdaptationService"/> 判定安全默认）时按 <see cref="None"/> 处理，
        /// 未知字符串同样兜底 <see cref="None"/>（不让面板因为脏数据崩掉）。</summary>
        public static AdaptationInfo Describe(string adaptationId)
        {
            string key = string.IsNullOrEmpty(adaptationId) ? None : adaptationId;
            return Infos.TryGetValue(key, out AdaptationInfo info) ? info : Infos[None];
        }
    }
}
