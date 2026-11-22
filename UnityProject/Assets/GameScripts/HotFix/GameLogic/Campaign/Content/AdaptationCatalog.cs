using System.Collections.Generic;
using GameLogic.Localization;

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

        /// <summary>FG15-UX-03：文案存文本键（adaptation.&lt;id&gt;.name / source / hazard / counter），读取时按当前语言取。</summary>
        public readonly struct AdaptationInfo
        {
            public readonly string Id;
            private readonly string _nameKey, _sourceKey, _hazardKey, _counterKey;
            public string DisplayName => GameText.Get(_nameKey);
            /// <summary>信息来源——验收卡第2条点名字段，解释玩家为什么会看到这条情报。</summary>
            public string SourceText => GameText.Get(_sourceKey);
            /// <summary>具体危险——这次适应在战斗中会造成什么真实变化。</summary>
            public string HazardText => GameText.Get(_hazardKey);
            /// <summary>玩家可用应对建议——不是纯警告，给出可执行的对策。</summary>
            public string CounterHintText => GameText.Get(_counterKey);

            public AdaptationInfo(string id, string nameKey, string sourceKey, string hazardKey, string counterKey)
            {
                Id = id;
                _nameKey = nameKey;
                _sourceKey = sourceKey;
                _hazardKey = hazardKey;
                _counterKey = counterKey;
            }
        }

        private static readonly Dictionary<string, AdaptationInfo> Infos = new Dictionary<string, AdaptationInfo>
        {
            [None] = new AdaptationInfo(None, "adaptation.none.name", "adaptation.none.source", "adaptation.none.hazard", "adaptation.none.counter"),
            [HeatResistant] = new AdaptationInfo(HeatResistant, "adaptation.heat_resistant.name", "adaptation.heat_resistant.source", "adaptation.heat_resistant.hazard", "adaptation.heat_resistant.counter"),
            [Flanker] = new AdaptationInfo(Flanker, "adaptation.flanker.name", "adaptation.flanker.source", "adaptation.flanker.hazard", "adaptation.flanker.counter"),
            [JammerSupport] = new AdaptationInfo(JammerSupport, "adaptation.jammer_support.name", "adaptation.jammer_support.source", "adaptation.jammer_support.hazard", "adaptation.jammer_support.counter"),
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
