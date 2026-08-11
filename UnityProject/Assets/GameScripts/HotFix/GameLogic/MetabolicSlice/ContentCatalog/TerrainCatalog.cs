using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// 冻结总案 §5.4 v1 地形目录（8 条）。只登记 Id→固有 Tag，落地走既有 WorldEnvironment.AddTerrainTag；
    /// 不接生成算法（冻结文档已声明地形本职"不负责生成算法"）。
    /// </summary>
    public static class TerrainCatalog
    {
        private static readonly Dictionary<string, string[]> _defs = new Dictionary<string, string[]>
        {
            ["ter_floor"] = new string[0],
            ["ter_wet"] = new[] { "Wet" },
            ["ter_oil"] = new[] { "Oil" },
            ["ter_acid"] = new[] { "Acid" },
            ["ter_sugar"] = new[] { "SugarFilm" },
            ["ter_light"] = new[] { "Light" },
            ["ter_shadow"] = new[] { "Shadow" },
            ["ter_salt"] = new[] { "SaltFrost" },
        };

        public static string[] GetTags(string id) => _defs.TryGetValue(id, out var tags) ? tags : null;

        public static IEnumerable<string> AllIds => _defs.Keys;
    }
}
