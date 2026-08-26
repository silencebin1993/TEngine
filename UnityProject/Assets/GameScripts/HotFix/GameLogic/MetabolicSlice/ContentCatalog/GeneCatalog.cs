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

        /// <summary>CATALOG-v3 §B1+§B2，24 条保留 + 12 条 story-004 新增 = 36 条，id/显示名/文案逐字取自
        /// 设计表；CreateModule 用 ComposeEngine.Builtin.Modules 的一等字段模块组合（多步用 CompositeModule
        /// 打包），不新增美术（R6，ArtId 未登记进 SimVisualLibrary，落兜底表现）。</summary>
        private static readonly Dictionary<string, (string DisplayName, string ArtId, string Description, Func<IModule> CreateModule)> _moduleDefs =
            new Dictionary<string, (string, string, string, Func<IModule>)>
            {
                // organ-gene-rebalance-v3 story-003：org_taxis 退役（preflight R8），其 Homing 强度
                // 0.9f 上调本基因至 0.85f 承接（非满值 0.9，Preflight 定的折中，见 story-003 Required 2）。
                ["gene_taxis"] = ("趋化导引", "gene/taxis", "需要器官。改装：攻击会拐向敌人。",
                    () => new HomingModule(0.85f)),
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

                // organ-gene-rebalance-v3 story-004：CATALOG-v3 §B2 新增 18 条中本 story 落地的 12 条，
                // 全部限定为"仅 Composite 现有 Module"（preflight R9 已把 Ripple/Rhythm/Drift/Capillary/
                // Catalyst/Weave 6 个新 Module 类 + gene_drift/gene_capillary 排除到本 story 之外，
                // 留给 005+ 落地新 Module 后再接线）。不新建任何 IModule 实现类。
                ["gene_fan"] = ("扇形", "gene/fan", "需要器官。改装：让扇面变宽或变窄。",
                    () => new SpreadModule(60f)),
                // 涡旋＝Pull（既有 gene_pull 同款字段）+ OrbitSpin（既有 gene_flagella 同款字段）叠加，
                // "边转边吸"不新增字段。
                ["gene_vortex"] = ("涡旋", "gene/vortex", "需要器官。改装：边转边把敌人往里吸。",
                    () => new CompositeModule("gene_vortex_mod", "涡旋", new PullModule(0.6f), new OrbitSpin(90f))),
                // 油/糖/酸/霜四条膜基因同款 Linger+TagAttach 骨架（同 gene_tide 的 Linger+Wet），Tag 名对齐
                // EnvironmentReactionCatalog 已注册的 "Oil"（env_oil_fire_burn），糖/酸/霜三个反应规则留给
                // 后续化学 story 接线，本 story 只扩 Tag 入口（CATALOG §E）。
                ["gene_oilfilm"] = ("油膜", "gene/oilfilm", "需要器官。改装：命中处留下油膜，遇火会烧起来。",
                    () => new CompositeModule("gene_oilfilm_mod", "油膜", new LingerModule(3f), new TagAttach("Oil"))),
                ["gene_sugarfilm"] = ("糖膜", "gene/sugarfilm", "需要器官。改装：命中处留下糖膜，遇酸会变粘滞。",
                    () => new CompositeModule("gene_sugarfilm_mod", "糖膜", new LingerModule(3f), new TagAttach("SugarFilm"))),
                ["gene_acidfilm"] = ("酸蚀膜", "gene/acidfilm", "需要器官。改装：落点变成酸坑，持续腐蚀。",
                    () => new CompositeModule("gene_acidfilm_mod", "酸蚀膜", new LingerModule(3f), new TagAttach("Acid"))),
                // R4 禁冻死：Tag 只叫 "Frozen" 做后续"碎裂加伤"化学反应的入口，霜膜本身不写任何减速/致死字段。
                ["gene_frostfilm"] = ("霜膜", "gene/frostfilm", "需要器官。改装：命中处结一层霜（不致死），配合物理命中更脆。",
                    () => new CompositeModule("gene_frostfilm_mod", "霜膜", new LingerModule(3f), new TagAttach("Frozen"))),
                // 谐振＝Echo（残影再打一次）+ Scatterer（多打几份，随 Mult 走高）叠加，"残影次数随 Count 增"
                // 用既有 Count 字段近似表达，不新建"按 Count 缩放 Echo 次数"的专用字段。
                ["gene_harmonic"] = ("谐振", "gene/harmonic", "需要器官。改装：命中点按节奏多打几次，打得越散回响越多。",
                    () => new CompositeModule("gene_harmonic_mod", "谐振", new EchoModule(0.4f), new Scatterer(3))),
                // 膜反弹＝Bounce（同 gene_mirror 的反弹字段）+ 贴 Tag 标出"撞墙"这一具体来源，与 gene_mirror
                // 的 Tag "Mirror" 区分开，供宿主结算时判定是否额外记一次撞墙伤害。
                ["gene_membrane"] = ("膜反弹", "gene/membrane", "需要器官。改装：撞墙会弹回来，还带一下伤。",
                    () => new CompositeModule("gene_membrane_mod", "膜反弹", new BounceModule(2), new TagAttach("WallImpact"))),
                // 迟绽＝Echo（Delay 落点二次结算）+ Split（命中分裂），"延迟后再分裂"用这两个既有字段直接拼。
                ["gene_bloomlate"] = ("迟绽", "gene/bloomlate", "需要器官。改装：先飞一会儿，稍后才裂开。",
                    () => new CompositeModule("gene_bloomlate_mod", "迟绽", new EchoModule(0.6f), new SplitModule(3))),
                // 抛投：Preflight R9 判定"Composite 或 ArcModule"，本 story 明确不建新 ArcModule，改用既有
                // BallisticsModule 的 Gravity 旋钮走抛物线弹道，不新增字段/类。
                ["gene_arc"] = ("抛投", "gene/arc", "需要器官。改装：攻击像抛物线一样落下。",
                    () => new CompositeModule("gene_arc_mod", "抛投", new BallisticsModule(speed: 1f, lifetime: 1.2f, gravity: 4f))),
                // 磁聚：真正的"多段弹道向一点收敛"是 005+ 新 MagnetModule 的范围，本 story 按 Required 只做
                // 弱化占位——复用既有 PullModule 给一个比 gene_vortex 更弱的单向牵引，贴 Tag "Magnet" 方便
                // 后续替换成专用 Module 时定位。
                ["gene_magnet"] = ("磁聚", "gene/magnet", "需要器官。改装：弹道边飞边被拉向一点（弱化占位，等专用磁聚机制）。",
                    () => new CompositeModule("gene_magnet_mod", "磁聚", new PullModule(0.3f), new TagAttach("Magnet"))),
                // 散播：preflight R9 已定案，复用 gene_lyso 的 ExplodeOnHit 触发时机 + gene_tide 的 Linger
                // 落点留坑，不新建 Seed 类。
                ["gene_scatterseed"] = ("散播", "gene/scatterseed", "需要器官。改装：命中会在旁边炸出小坑。",
                    () => new CompositeModule("gene_scatterseed_mod", "散播", new ExplodeOnHit(), new LingerModule(2f))),
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

        /// <summary>Module 基因全集，CATALOG-v3 §B1+§B2 36 条（24 保留 + story-004 新增 12）。</summary>
        public static IEnumerable<string> AllModuleIds => _moduleDefs.Keys;

        /// <summary>Contract + Module 全集，本 story 起等价于 <see cref="AllModuleIds"/>（36 条）。</summary>
        public static IEnumerable<string> AllGeneIds => _defs.Keys.Concat(_moduleDefs.Keys);
    }
}
