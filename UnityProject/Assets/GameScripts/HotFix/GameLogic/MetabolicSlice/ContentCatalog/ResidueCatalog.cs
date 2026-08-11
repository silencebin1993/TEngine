using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// 冻结总案 §5.5 v1 残留目录（6 条）。只登记 Id→(Tag, 默认 Ttl)，落地走既有 ResidueStack/
    /// WorldEnvironment.AddResidue；`res_sticky` 是 Acid+SugarFilm 复合，简化为单 Tag "StickyAcid"（同冻结表标注）。
    /// </summary>
    public static class ResidueCatalog
    {
        private static readonly Dictionary<string, (string Tag, int DefaultTtl)> _defs =
            new Dictionary<string, (string, int)>
            {
                ["res_steam"] = ("Steam", 3),
                ["res_burn"] = ("Fire", 4),
                ["res_oil"] = ("Oil", 5),
                ["res_acid"] = ("Acid", 4),
                ["res_sticky"] = ("StickyAcid", 4),
                ["res_charge"] = ("Shock", 2),
            };

        public static (string Tag, int DefaultTtl)? Get(string id) =>
            _defs.TryGetValue(id, out var d) ? d : ((string, int)?)null;

        public static IEnumerable<string> AllIds => _defs.Keys;
    }
}
