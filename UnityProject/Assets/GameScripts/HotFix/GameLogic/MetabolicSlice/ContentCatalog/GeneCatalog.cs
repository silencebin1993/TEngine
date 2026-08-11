using System;
using System.Collections.Generic;
using ChemEngine.Builtin.Contracts;
using ChemEngine.Core;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// 冻结总案 §5.3 v1 基因目录（11 条）。直接复用既有 12 个 ChemEngine.Builtin.Contracts，不新增契约类；
    /// `TempoAccel` 是 12 个 Contract 中唯一无冻结 Gene 对应者，保留可用但不强塞映射（见冻结决策 G2）。
    /// </summary>
    public static class GeneCatalog
    {
        private static readonly Dictionary<string, (string DisplayName, string ArtId, Func<IContract> CreateContract)> _defs =
            new Dictionary<string, (string, string, Func<IContract>)>
            {
                ["gene_double"] = ("双倍表达", "gene/double", () => new OverloadPermit(2.0f)),
                ["gene_pyro"] = ("燃律", "gene/pyro", () => new BurnLaw()),
                ["gene_tide"] = ("潮律", "gene/tide", () => new WetLaw()),
                ["gene_volt"] = ("雷律", "gene/volt", () => new ShockLaw()),
                ["gene_delay"] = ("延偿", "gene/delay", () => new DelayedPayment()),
                ["gene_mirror"] = ("镜界", "gene/mirror", () => new MirrorRealm()),
                ["gene_mute"] = ("哑火", "gene/mute", () => new Misfire()),
                ["gene_swarm"] = ("寡兵", "gene/swarm", () => new FewButFierce()),
                ["gene_blood"] = ("血债", "gene/blood", () => new BloodDebt()),
                ["gene_edge"] = ("绝境", "gene/edge", () => new Desperation()),
                ["gene_share"] = ("共相", "gene/share", () => new SharedFate()),
            };

        public static Func<IContract> Get(string id) => _defs.TryGetValue(id, out var d) ? d.CreateContract : null;

        public static string GetDisplayName(string id) => _defs.TryGetValue(id, out var d) ? d.DisplayName : null;

        public static string GetArtId(string id) => _defs.TryGetValue(id, out var d) ? d.ArtId : null;

        public static IEnumerable<string> AllIds => _defs.Keys;
    }
}
