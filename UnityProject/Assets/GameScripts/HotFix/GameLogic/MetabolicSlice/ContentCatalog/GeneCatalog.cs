using System;
using System.Collections.Generic;
using System.Linq;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>
    /// combat-identity-rework story-004：CATALOG §B v2 基因目录（24 条 gene_*，全部 Module 类型）。
    /// 取代冻结总案 §5.3（11 条 Contract 基因）与 organ-socket-slice 引入的 19 条 org_* 修饰副表。
    /// `gene_double`/`gene_mute`/`gene_edge`/`gene_share`（纯数值倍率/开关，DESIGN.md 判定"太薄"）
    /// 是本 story 明确要求删除的空壳，本目录不再授予；旧 `gene_pyro/tide/volt/mirror/swarm/blood`
    /// 6 个 id 复用但效果按 CATALOG §B 重写为 Module 字段+Tag 组合（不再是 RuleVector 数值旋钮，
    /// 见各条注释），`gene_delay` 被 `gene_echo` 取代（Required 5：残影是命中点二次结算，不是
    /// "伤害推迟到账"）。旧 19 条 `org_*` 修饰按 CATALOG §C 迁移为下方对应 `gene_*`（`org_valve`/
    /// `org_filter` 是删除而非迁移，见 OrganelleCatalog）。
    /// `ComposeEngine.Builtin.Contracts` 下的 12 个 Contract 类文件本身不删——它们是引擎可复用基元，
    /// 仍被 ComposeEngine.Tests 覆盖；"删除空壳"删的是 GameLogic 层的授予入口，不是引擎库源码。
    /// `_defs`（Contract 表）保留结构但恒为空：`CarrierCompiler.Get` 对任何 gene_* 都会 miss 并回落
    /// `GetModule` 查表——不改 `CarrierCompiler.cs`（005 范围，preflight R2/Dependencies 已定）。
    /// </summary>
    public static class GeneCatalog
    {
        private static readonly Dictionary<string, (string DisplayName, string ArtId, string Description, Func<IContract> CreateContract)> _defs =
            new Dictionary<string, (string, string, string, Func<IContract>)>();

        /// <summary>CATALOG §B 24 条，id/显示名/文案逐字取自设计表；CreateModule 用
        /// ComposeEngine.Builtin.Modules 的一等字段模块组合（多步用 CompositeModule 打包），
        /// 不新增美术（R6，ArtId 未登记进 SimVisualLibrary，落兜底表现）。</summary>
        private static readonly Dictionary<string, (string DisplayName, string ArtId, string Description, Func<IModule> CreateModule)> _moduleDefs =
            new Dictionary<string, (string, string, string, Func<IModule>)>
            {
                ["gene_taxis"] = ("趋化导引", "gene/taxis", "需要器官。改装：攻击会拐向敌人。",
                    () => new HomingModule(0.6f)),
                ["gene_spindle"] = ("纺锤分裂", "gene/spindle", "需要器官。改装：一次打出更多、更散。",
                    () => new CompositeModule("gene_spindle_mod", "纺锤分裂", new Scatterer(2), new SpreadModule(30f))),
                ["gene_elastic"] = ("弹性壁", "gene/elastic", "需要器官。改装：撞边或撞敌会弹。",
                    () => new BounceModule(1)),
                ["gene_tubule"] = ("微管贯穿", "gene/tubule", "需要器官。改装：穿过目标继续飞。",
                    () => new PierceModule(1)),
                ["gene_return"] = ("回旋外壳", "gene/return", "需要器官。改装：打出的东西会飞回来。",
                    () => new ReturnModule()),
                ["gene_swell"] = ("膨胀泡", "gene/swell", "需要器官。改装：体变大、圈变大、坑变大。",
                    () => new Grow(1.5f)),
                ["gene_lyso"] = ("溶酶壳", "gene/lyso", "需要器官。改装：命中再爆一圈。",
                    () => new ExplodeOnHit()),
                // Spin（自转）+ Orbit（绕轨半径）是同一个"绕圈"设定的两个旋钮，OrbitRadiusModule
                // 是本 story 新增的 Orbit 字段写手（此前 002 只声明了字段，无人写）。
                ["gene_flagella"] = ("鞭毛绕", "gene/flagella", "需要器官。改装：攻击绕圈飞/绕着你转。",
                    () => new CompositeModule("gene_flagella_mod", "鞭毛绕", new OrbitSpin(120f), new OrbitRadiusModule(1.5f))),
                // Required 5：残影＝命中点稍后自己再打一次（复用 Actuator 已读的 Payload["Delay"]），
                // 不是旧 DelayedPayment 契约"把本次伤害到账时间推迟"的语义。
                ["gene_echo"] = ("残影", "gene/echo", "需要器官。改装：命中点稍后自己再打一次。",
                    () => new EchoModule(0.4f)),
                // Required 4：元素三基因改世界，不是只 BurnAdd+=1。Trail 是真实沿途伤害（飞行路径本身
                // 变成火径），Tag "Fire" 接入既有 EnvironmentReactionCatalog（Fire+Wet→蒸汽残留、
                // Fire+Oil→燃烧区残留），比旧 BurnLaw 纯数值更"改世界"。
                ["gene_pyro"] = ("燃径", "gene/pyro", "需要器官。改装：飞过或命中铺火径，不是只挂燃烧数字。",
                    () => new CompositeModule("gene_pyro_mod", "燃径", new TrailModule(2f), new TagAttach("Fire"))),
                // Linger 在命中点留下持续结算的水坑（真实世界残留），Tag "Wet" 同样接入上述环境反应表。
                ["gene_tide"] = ("潮洼", "gene/tide", "需要器官。改装：留下水洼；水洼让雷跳、让火变蒸汽。",
                    () => new CompositeModule("gene_tide_mod", "潮洼", new LingerModule(3f), new TagAttach("Wet"))),
                // Chain 触发内核已有连锁命中；Tag "Shock" 与 SubstanceWeights 内置 Charge 维叠加，
                // 目标带 Wet 时子系统自然产出更强的物质向量（雷水协同），不额外接新反应规则。
                ["gene_volt"] = ("雷桥", "gene/volt", "需要器官。改装：优先沿湿面/水洼跳。",
                    () => new CompositeModule("gene_volt_mod", "雷桥", new ChainModule(2), new TagAttach("Shock"))),
                ["gene_vacuole"] = ("蓄能液泡", "gene/vacuole", "需要器官。改装：少打几下，下一发更肥。",
                    () => new CompositeModule("gene_vacuole_mod", "蓄能液泡", new Capacitor(1.6f), new Grow(1.3f))),
                ["gene_golgi"] = ("分流芽", "gene/golgi", "需要器官。改装：半路或出手时分叉。",
                    () => new SplitModule(2)),
                // Required 4 同理：过热不再默默降温，而是清空热量并转成一圈瞬时脉冲，替代旧
                // org_radiator（HeatSink）/org_breaker（Fuse）静默效果，见 HeatShockModule。
                ["gene_heatshock"] = ("热激褶", "gene/heatshock", "需要器官。改装：过热时爆一圈，而不是「默默降温」。",
                    () => new HeatShockModule()),
                ["gene_synapse"] = ("突触连发", "gene/synapse", "需要器官。改装：命中立刻再挤一发。",
                    () => new FeedbackLoop(FeedbackMode.Hit)),
                // Required：血珠不是"暗改吸血百分比"（旧 BloodDebt 契约）。复用 Return 字段表达"打出的
                // 伤害溅出血珠飞回"这一具体可见的运动轨迹，宿主结算时按 Tag "Blood" 把回程结算成治疗
                // 而非二次伤害。
                ["gene_blood"] = ("血珠", "gene/blood", "需要器官。改装：打出的伤害溅出血珠飞回治疗。",
                    () => new CompositeModule("gene_blood_mod", "血珠", new ReturnModule(), new TagAttach("Blood"))),
                // 召唤物继承宿主器官打法：只贴标记，具体"召唤物读取 Tag 换打法"留给召唤生成管线（006+）。
                ["gene_swarm"] = ("群体感应", "gene/swarm", "需要器官。改装：你的召唤物学会这件器官的打法。",
                    () => new TagAttach("InheritPattern")),
                ["gene_slime"] = ("粘液拖尾", "gene/slime", "需要器官。改装：路径减速并伤。",
                    () => new CompositeModule("gene_slime_mod", "粘液拖尾", new TrailModule(1f), new TagAttach("Slow"))),
                ["gene_pull"] = ("吸引素", "gene/pull", "需要器官。改装：把敌人往弹/圈/坑里吸。",
                    () => new PullModule(0.5f)),
                ["gene_split"] = ("绽放", "gene/split", "需要器官。改装：命中裂成小弹。",
                    () => new SplitModule(3)),
                ["gene_mirror"] = ("镜面", "gene/mirror", "需要器官。改装：弹会反弹；近战则弹开敌人的弹。",
                    () => new CompositeModule("gene_mirror_mod", "镜面", new BounceModule(2), new TagAttach("Mirror"))),
                ["gene_receptor"] = ("受体记忆", "gene/receptor", "需要器官。改装：打过的敌人会被记住，后续更会追它。",
                    () => new CompositeModule("gene_receptor_mod", "受体记忆", new HomingModule(0.5f), new TagAttach("ReceptorMemory"))),
                // Required：低血爆炸，禁即死冻结（R4）。只贴 ExplodeOnHit+Tag，是否"残血"由宿主结算时判定。
                ["gene_apoptosis"] = ("凋亡信号", "gene/apoptosis", "需要器官。改装：残血敌人被打中会爆开，不是冻死。",
                    () => new CompositeModule("gene_apoptosis_mod", "凋亡信号", new ExplodeOnHit(), new TagAttach("Apoptosis"))),
            };

        public static Func<IContract> Get(string id) => _defs.TryGetValue(id, out var d) ? d.CreateContract : null;

        /// <summary>CarrierCompiler 的 Module 分支查表入口。查不到返回 null。</summary>
        public static Func<IModule> GetModule(string id) => _moduleDefs.TryGetValue(id, out var d) ? d.CreateModule : null;

        public static string GetDisplayName(string id) =>
            _defs.TryGetValue(id, out var d) ? d.DisplayName :
            _moduleDefs.TryGetValue(id, out var m) ? m.DisplayName : null;

        public static string GetArtId(string id) =>
            _defs.TryGetValue(id, out var d) ? d.ArtId :
            _moduleDefs.TryGetValue(id, out var m) ? m.ArtId : null;

        /// <summary>图鉴/tooltip 用的中文一句话机制说明，第一句固定「需要器官。改装：…」（CATALOG §B）。</summary>
        public static string GetDescription(string id) =>
            _defs.TryGetValue(id, out var d) ? d.Description :
            _moduleDefs.TryGetValue(id, out var m) ? m.Description : null;

        /// <summary>Contract 基因全集，本 story 起恒为空集合（保留 API 形状供调用方兼容）。</summary>
        public static IEnumerable<string> AllIds => _defs.Keys;

        /// <summary>Module 基因全集，CATALOG §B 24 条。</summary>
        public static IEnumerable<string> AllModuleIds => _moduleDefs.Keys;

        /// <summary>Contract + Module 全集，本 story 起等价于 <see cref="AllModuleIds"/>（24 条）。</summary>
        public static IEnumerable<string> AllGeneIds => _defs.Keys.Concat(_moduleDefs.Keys);

        /// <summary>story-004：24 条基因全部 Module 类型，不再对应任何 OrganelleCatalog 条目（它们
        /// 不是器官），旧的"借 Role 分组"语义已失效。007（copy-ui-retire）会改成 AttackFamily 分组；
        /// 本 story 只保证不 NPE，恒返回 null（None 态，调用方 switch/?? 均已 null-safe）。</summary>
        public static string GetVisualGroup(string geneId) => null;
    }
}
