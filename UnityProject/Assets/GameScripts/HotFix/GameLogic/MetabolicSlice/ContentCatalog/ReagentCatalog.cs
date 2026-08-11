using System;
using System.Collections.Generic;
using GameLogic.MetabolicSlice.Digestion;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// 冻结总案 §5.8 v1 试剂目录（5 条）。Id→Func&lt;Reagent&gt;，复用既有 Digestion/Reagent.cs 构造，不改该文件。
    /// `rg_toxic_bile` Toxicity 设为达阈值（DigestionChamber.ToxicityFailThreshold=1f），对应冻结表"失败率高"
    /// （本引擎消化结算是阈值判定，无随机数，"高"在此简化为确定失败）。
    /// </summary>
    public static class ReagentCatalog
    {
        private static readonly Dictionary<string, Func<Reagent>> _defs = new Dictionary<string, Func<Reagent>>
        {
            ["rg_scrap_mito"] = () => new Reagent(
                "rg_scrap_mito", null, new HashSet<string>(), 0.1f, 2, new List<string> { "org_mito" }),
            ["rg_scrap_lens"] = () => new Reagent(
                "rg_scrap_lens", null, new HashSet<string>(), 0.1f, 2, new List<string> { "org_lens" }),
            ["rg_toxic_bile"] = () => new Reagent(
                "rg_toxic_bile", null, new HashSet<string> { "Acid" }, 1f, 3, new List<string>()),
            ["rg_fatty"] = () => new Reagent(
                "rg_fatty", null, new HashSet<string> { "Oil" }, 0.1f, 4, new List<string> { "org_swell" }),
            ["rg_nerve"] = () => new Reagent(
                "rg_nerve", null, new HashSet<string> { "Shock" }, 0.1f, 4, new List<string> { "org_ion", "org_synapse" }),
        };

        public static Func<Reagent> Get(string id) => _defs.TryGetValue(id, out var f) ? f : null;

        public static IEnumerable<string> AllIds => _defs.Keys;
    }
}
