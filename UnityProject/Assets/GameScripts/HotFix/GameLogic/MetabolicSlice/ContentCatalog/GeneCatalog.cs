using System;
using System.Collections.Generic;
using System.Linq;
using ComposeEngine.Builtin.Contracts;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// 冻结总案 §5.3 v1 基因目录（11 条 Contract）+ story-004 D11 新增 Module 分支基因副表（19 条）。
    /// Contract 表直接复用既有 12 个 ComposeEngine.Builtin.Contracts，不新增契约类；
    /// `TempoAccel` 是 12 个 Contract 中唯一无冻结 Gene 对应者，保留可用但不强塞映射（见冻结决策 G2）。
    /// </summary>
    public static class GeneCatalog
    {
        private static readonly Dictionary<string, (string DisplayName, string ArtId, string Description, Func<IContract> CreateContract)> _defs =
            new Dictionary<string, (string, string, string, Func<IContract>)>
            {
                ["gene_double"] = ("双倍表达", "gene/double", "伤害倍率提升，并在结算中留痕标记过载。", () => new OverloadPermit(2.0f)),
                ["gene_pyro"] = ("燃律", "gene/pyro", "命中附加燃烧强度。", () => new BurnLaw()),
                ["gene_tide"] = ("潮律", "gene/tide", "命中附加潮湿强度。", () => new WetLaw()),
                ["gene_volt"] = ("雷律", "gene/volt", "命中附加感电强度。", () => new ShockLaw()),
                ["gene_delay"] = ("延偿", "gene/delay", "伤害结算延后；多个延偿叠加取最大延迟而非累加。", () => new DelayedPayment()),
                ["gene_mirror"] = ("镜界", "gene/mirror", "开启完全反射。", () => new MirrorRealm()),
                ["gene_mute"] = ("哑火", "gene/mute", "禁用远程攻击，并强制关闭反射。", () => new Misfire()),
                ["gene_swarm"] = ("寡兵", "gene/swarm", "单位越少输出曲线越陡（群体强度乘子）。", () => new FewButFierce()),
                ["gene_blood"] = ("血债", "gene/blood", "造成伤害的一部分转化为治疗。", () => new BloodDebt()),
                ["gene_edge"] = ("绝境", "gene/edge", "残血时承受伤害打折。", () => new Desperation()),
                ["gene_share"] = ("共相", "gene/share", "队伍共享承伤状态。", () => new SharedFate()),
            };

        /// <summary>story-004 D4/D15：Module 分支基因总数 = 19 条（A 组 8 + B 组 11），沿用现有器官 id 原文
        /// （不改名成 gene_*，理由见 preflight D15）。org_insulate（D16 退役）与 org_mito/org_chloro
        /// （D18 并入 Carrier 内建）刻意不在此列，即不进入可装备插槽的 Module 副表。</summary>
        private static readonly string[] _moduleGeneIds =
        {
            // A 组（R5 已定案 8 条）
            "org_vacuole", "org_golgi", "org_merge", "org_radiator", "org_breaker", "org_synapse", "org_valve", "org_filter",
            // B 组（W9 修饰器官 11 条）
            "org_scatter", "org_swell", "org_flagella", "org_lyso", "org_perox", "org_aqua", "org_ion", "org_lens", "org_slime", "org_receptor", "org_spine",
        };

        /// <summary>D11 方案 (a)：同表加副表。DisplayName/ArtId/Description/CreateModule 直接委派 OrganelleCatalog——
        /// 基因目录不重复存这四份数据，避免与器官定义漂移。</summary>
        private static readonly Dictionary<string, (string DisplayName, string ArtId, string Description, Func<IModule> CreateModule)> _moduleDefs =
            BuildModuleDefs();

        private static Dictionary<string, (string, string, string, Func<IModule>)> BuildModuleDefs()
        {
            var dict = new Dictionary<string, (string, string, string, Func<IModule>)>();
            foreach (var id in _moduleGeneIds)
            {
                var def = OrganelleCatalog.Get(id);
                dict[id] = (def.DisplayName, def.ArtId, def.Description, def.CreateModule);
            }
            return dict;
        }

        public static Func<IContract> Get(string id) => _defs.TryGetValue(id, out var d) ? d.CreateContract : null;

        /// <summary>D14：CarrierCompiler 的 Module 分支查表入口。查不到返回 null（未命中 = 004 迁徙前占位/拼写错误）。</summary>
        public static Func<IModule> GetModule(string id) => _moduleDefs.TryGetValue(id, out var d) ? d.CreateModule : null;

        /// <summary>D12：先查 Contract 表，未命中再查 Module 表（005 UI 显示名要覆盖两类基因）。</summary>
        public static string GetDisplayName(string id) =>
            _defs.TryGetValue(id, out var d) ? d.DisplayName :
            _moduleDefs.TryGetValue(id, out var m) ? m.DisplayName : null;

        /// <summary>D12：先查 Contract 表，未命中再查 Module 表。</summary>
        public static string GetArtId(string id) =>
            _defs.TryGetValue(id, out var d) ? d.ArtId :
            _moduleDefs.TryGetValue(id, out var m) ? m.ArtId : null;

        /// <summary>story-002 D1：图鉴/tooltip 用的中文一句话机制说明。同 GetDisplayName 双表回退写法。</summary>
        public static string GetDescription(string id) =>
            _defs.TryGetValue(id, out var d) ? d.Description :
            _moduleDefs.TryGetValue(id, out var m) ? m.Description : null;

        /// <summary>D13：保持只列 Contract 基因不变，语义不扩容——现有调用点可能依赖这条不含 Module 分支基因。</summary>
        public static IEnumerable<string> AllIds => _defs.Keys;

        /// <summary>D13：Module 分支基因全集（19 条），供 005 UI / 008 冒烟册用。</summary>
        public static IEnumerable<string> AllModuleIds => _moduleDefs.Keys;

        /// <summary>D13：Contract + Module 两类基因全集串接，供 005 UI / 008 冒烟册用。</summary>
        public static IEnumerable<string> AllGeneIds => _defs.Keys.Concat(_moduleDefs.Keys);

        /// <summary>carrier-visual-feedback story-002：本体视觉分组查询。Contract 基因（无 Role）归并为
        /// "Contract" 兜底组；Module 基因直接复用 <see cref="OrganelleCatalog.OrganelleDef.Role"/>
        /// （"Relay"/"Transform"/"Edge"），零新增分类表。查不到返回 null（None 态）。</summary>
        public static string GetVisualGroup(string geneId)
        {
            if (_defs.ContainsKey(geneId))
            {
                return "Contract";
            }
            if (_moduleDefs.ContainsKey(geneId))
            {
                return OrganelleCatalog.Get(geneId)?.Role.ToString();
            }
            return null;
        }
    }
}
