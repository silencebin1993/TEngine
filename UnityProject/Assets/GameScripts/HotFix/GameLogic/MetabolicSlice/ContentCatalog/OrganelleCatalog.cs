using System;
using System.Collections.Generic;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.MetabolicSlice.Structural;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    public enum OrganelleRole { Source, Relay, Transform, Sink, Edge }

    public enum OrganelleAttachTarget { Slot, DirectedEdge }

    /// <summary>organelle-structural-tier story-001 Required 1：分类字段，独立于既有攻击/基因链路。</summary>
    public enum OrganelleCategory { Attack, Structural, EnergySource }

    /// <summary>冻结总案 §5.2 器官定义。CreateModule=null 代表暂不可执行，仅注册元数据可查询/可装备占位（见 N1）。</summary>
    public sealed class OrganelleDef
    {
        public string Id { get; }
        public string DisplayName { get; }
        /// <summary>story-002 D1：图鉴/tooltip 用的中文一句话机制说明。</summary>
        public string Description { get; }
        public OrganelleRole Role { get; }
        public OrganelleAttachTarget AttachTarget { get; }

        /// <summary>null = 不限地板（AttachTarget=Slot 时生效；DirectedEdge 恒 null）。</summary>
        public HashSet<SlotType> AllowedSlotTypes { get; }

        public string ArtId { get; }
        public Func<IModule> CreateModule { get; }

        /// <summary>是否为 organ-socket-slice 的 Carrier 器官（D7：独立语义位，不用 Role==Sink 判定——
        /// 今天两者外延重合是巧合，未来新增 Sink 器官不应默认变成 Carrier）。</summary>
        public bool IsCarrier { get; }

        /// <summary>story-004 D16：退役标记。仅从「可装备插槽集」（GeneCatalog Module 副表）排除，
        /// 不从 <see cref="OrganelleCatalog"/>._defs 删条目——IdSmokeReport 等既有探针按 id 遍历，删条目会静默少测。</summary>
        public bool IsRetired { get; }

        /// <summary>combat-identity-rework story-003（preflight R2）：独立开火测试通过的攻击方式，与
        /// <see cref="IsCarrier"/> 分开存——迁移期两者未必相等（旧修饰暂留 IsCarrier=true 数据，
        /// AttackMethod=false 才是"是否出现在攻击方式栏"的最终判据）。终态目标 IsCarrier==AttackMethod，
        /// 004/006/007 逐步把旧修饰的 IsCarrier 也收敛为 false。</summary>
        public bool AttackMethod { get; }

        /// <summary>story-007（R7）：CATALOG §A1/§A2 的 Family/Pattern 列，驱动 Carrier 本体视觉分组
        /// （取代已废弃的按装备基因 Role 分组，见 <see cref="GameLogic.Battle.Feedback.CarrierBodyVisualPresenter"/>）。
        /// 非攻击器官（能量核心/已退役旧修饰）恒 null。</summary>
        public string AttackFamily { get; }

        /// <summary>story-001 Required 1：新分类字段，默认 Attack——不强制回填既有 48 条。</summary>
        public OrganelleCategory Category { get; }

        /// <summary>story-001 Required 2：结构器官的常驻被动加成，复用 GameLogic.Stats.StatModifier，
        /// 不新造加成结构。非 Structural 分类恒为 null。</summary>
        public StatModifier[] StructuralEffects { get; }

        /// <summary>story-009（R4/Preflight D7）：受击/移动/击杀/血量阈值/周期触发的一次性或周期性效果
        /// （DESIGN §9.6 五种钩子），与 <see cref="StructuralEffects"/> 并存、可同非空。默认 null——
        /// 本 story 只声明字段，不注册任何 §A2 条目实际赋值（010/011 消费）。</summary>
        public TriggerHookSpec? TriggerHook { get; }

        public OrganelleDef(string id, string displayName, OrganelleRole role, OrganelleAttachTarget attachTarget,
            IEnumerable<SlotType> allowedSlotTypes, string artId, Func<IModule> createModule, bool isCarrier = false,
            bool isRetired = false, string description = "", bool attackMethod = false, string attackFamily = null,
            OrganelleCategory category = OrganelleCategory.Attack, StatModifier[] structuralEffects = null,
            TriggerHookSpec? triggerHook = null)
        {
            Id = id;
            DisplayName = displayName;
            Role = role;
            AttachTarget = attachTarget;
            AllowedSlotTypes = allowedSlotTypes == null ? null : new HashSet<SlotType>(allowedSlotTypes);
            ArtId = artId;
            CreateModule = createModule;
            IsCarrier = isCarrier;
            IsRetired = isRetired;
            Description = description;
            AttackMethod = attackMethod;
            AttackFamily = attackFamily;
            Category = category;
            StructuralEffects = structuralEffects;
            TriggerHook = triggerHook;
        }
    }

    /// <summary>
    /// 冻结总案 §5.2 v1 器官目录（24 条）。17 条复用既有 ComposeEngine Module（构造参数不同）；
    /// 7 条（story-004 接线）用"受击/邻格=正向流水线标量近似"口径实现本职栏，不建真实事件总线 /
    /// 空间催化 / DirectedEdge 挂载执行管线，理由逐条见 story-004 Preflight D4~D10。
    /// </summary>
    public static class OrganelleCatalog
    {
        private static readonly SlotType[] MembraneOnly = { SlotType.Membrane };

        private static readonly Dictionary<string, OrganelleDef> _defs = new Dictionary<string, OrganelleDef>
        {
            ["org_mito"] = new OrganelleDef("org_mito", "线粒体", OrganelleRole.Source, OrganelleAttachTarget.Slot,
                null, "org/mito", () => new EnergyCore(),
                description: "装配链的能量起点，持续输出基础能量。"),
            ["org_chloro"] = new OrganelleDef("org_chloro", "叶绿体", OrganelleRole.Source, OrganelleAttachTarget.Slot,
                null, "org/chloro", () => new EnergyCore(),
                description: "装配链的能量起点，以光合方式持续输出基础能量。"),
            // ── combat-identity-rework story-007（R2/R5/R8）：以下 16 条已迁 gene_*（CATALOG §C），
            // IsCarrier 收敛为 false 并 isRetired:true——同 org_insulate/org_valve/org_filter 先例，
            // 仅从"可装备插槽集"排除，不删 _defs 条目（IdSmokeReport 等既有探针按 id 遍历，删条目会静默少测）。
            ["org_vacuole"] = new OrganelleDef("org_vacuole", "液泡电容", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/vacuole", () => new Capacitor(), isRetired: true,
                description: "按倍率放大流经的能量（已退役，效果迁 gene_vacuole）。"),
            ["org_golgi"] = new OrganelleDef("org_golgi", "高尔基分流", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/golgi", () => new Splitter(), isRetired: true,
                description: "把能量按支路数均分，供后续模块或多路执行器使用（已退役，效果迁 gene_golgi）。"),
            ["org_merge"] = new OrganelleDef("org_merge", "囊泡汇流", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/merge", () => new MergeInlet(), isRetired: true,
                description: "把入流能量夹到带宽上限，超出部分折半转为热量（已退役，并入 gene_vacuole）。"),
            ["org_lens"] = new OrganelleDef("org_lens", "晶状聚焦", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/lens", () => new FocusLens(), isRetired: true,
                description: "提升伤害的同时必定增加热量（已退役，武器身份拆为 org_lensbeam）。"),
            ["org_scatter"] = new OrganelleDef("org_scatter", "纺锤散射", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/scatter", () => new Scatterer(), isRetired: true,
                description: "按强度倍率增加命中次数（分裂/多段）（已退役，效果迁 gene_spindle）。"),
            ["org_swell"] = new OrganelleDef("org_swell", "膨胀泡", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/swell", () => new Grow(), isRetired: true,
                description: "弹体尺度随强度倍率变大，命中范围同步放大（已退役，效果迁 gene_swell）。"),
            ["org_flagella"] = new OrganelleDef("org_flagella", "鞭毛环", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/flagella", () => new OrbitSpin(), isRetired: true,
                description: "赋予弹体自旋角速度，改变弹道/绕轨轨迹（已退役，效果迁 gene_flagella）。"),
            ["org_lyso"] = new OrganelleDef("org_lyso", "溶酶爆", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/lyso", () => new ExplodeOnHit(), isRetired: true,
                description: "命中后触发爆炸效果（已退役，效果迁 gene_lyso）。"),
            ["org_perox"] = new OrganelleDef("org_perox", "过氧化物酶", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/perox", () => new TagAttach("Fire"), isRetired: true,
                description: "命中附加火焰标记（已退役，效果迁 gene_pyro）。"),
            ["org_aqua"] = new OrganelleDef("org_aqua", "水合泡", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/aqua", () => new TagAttach("Wet"), isRetired: true,
                description: "命中附加潮湿标记（已退役，效果迁 gene_tide）。"),
            ["org_ion"] = new OrganelleDef("org_ion", "离子泵", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/ion", () => new TagAttach("Shock"), isRetired: true,
                description: "命中附加感电标记（已退役，效果迁 gene_volt）。"),
            ["org_radiator"] = new OrganelleDef("org_radiator", "散热褶", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/radiator", () => new HeatSink(), isRetired: true,
                description: "持续压低当前热量，不低于 0（已退役，并入 gene_heatshock）。"),
            ["org_breaker"] = new OrganelleDef("org_breaker", "热休克闸", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/breaker", () => new Fuse(), isRetired: true,
                description: "能量超过安全上限时跳闸夹住，防止后续模块继续放大溢出（已退役，并入 gene_heatshock）。"),
            ["org_synapse"] = new OrganelleDef("org_synapse", "突触反馈", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/synapse", () => new FeedbackLoop(FeedbackMode.Hit), isRetired: true,
                description: "把命中能量的一部分回灌自身，按预算封顶防止无限自激（已退役，效果迁 gene_synapse）。"),
            ["org_emitter"] = new OrganelleDef("org_emitter", "分泌喷射器", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/emitter", () => new CompositeModule("emitter_attack", "分泌喷射器攻击",
                    new BallisticsModule(speed: 1.3f), new Actuator(pattern: AttackPattern.Projectile)),
                isCarrier: true, attackMethod: true, attackFamily: "Projectile",
                description: "攻击方式：朝瞄准方向射出代谢弹。"),
            ["org_cilia"] = new OrganelleDef("org_cilia", "纤毛刺", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/cilia", () => new CompositeModule("cilia_attack", "纤毛刺攻击",
                    new SpreadModule(40f), new Actuator(shape: "Melee", pattern: AttackPattern.Melee)),
                isCarrier: true, attackMethod: true, attackFamily: "Melee",
                description: "攻击方式：身前短锥挥刺。"),
            ["org_spine"] = new OrganelleDef("org_spine", "刺突", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/spine", () => new CompositeModule("spine_attack", "刺突反击",
                    new Thorns(reflectDamage: 6f), new Actuator(shape: "Melee", pattern: AttackPattern.Thorns)),
                isCarrier: true, attackMethod: true, attackFamily: "Thorns",
                description: "攻击方式：被碰到或挨打时反刺，不靠主动开火。"),
            ["org_slime"] = new OrganelleDef("org_slime", "粘液层", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/slime", () => new TagAttach("Oil"), isRetired: true,
                description: "命中附加油污标记（已退役，效果迁 gene_slime）。"),
            ["org_receptor"] = new OrganelleDef("org_receptor", "受体", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/receptor", () => new TagAttach("Catalyst"), isRetired: true,
                description: "命中附加催化标记（已退役，效果迁 gene_receptor）。"),
            ["org_insulate"] = new OrganelleDef("org_insulate", "绝缘管", OrganelleRole.Edge, OrganelleAttachTarget.DirectedEdge,
                null, "org/insulate", () => new Insulator(), isRetired: true,
                description: "按比例阻尼热量（已退役，不进可装备插槽）。"),
            // combat-identity-rework story-004 Required 7 + CATALOG §C：删除（非迁移，不进 gene_*），
            // 收敛 isCarrier=false 并 isRetired=true——同 org_insulate 先例，仅从"可装备插槽集"排除，
            // 不删 _defs 条目（IdSmokeReport 等既有探针按 id 遍历，删条目会静默少测）。
            ["org_valve"] = new OrganelleDef("org_valve", "单向阀", OrganelleRole.Edge, OrganelleAttachTarget.Slot,
                null, "org/valve", () => new Valve(), isRetired: true,
                description: "强化沿链方向流动的能量（已退役，不进可装备插槽）。"),
            ["org_filter"] = new OrganelleDef("org_filter", "过滤管", OrganelleRole.Edge, OrganelleAttachTarget.Slot,
                null, "org/filter", () => new TagFilter("Approved", 0.5f), isRetired: true,
                description: "只放行带指定标记的能量，未命中的按比例折损（已退役，不进可装备插槽）。"),

            // ── combat-identity-rework story-003：CATALOG §A1 第一波 9 个新攻击方式 ──
            // 不新增美术：ArtId 未登记进 SimVisualLibrary，落 SphereUnit() 兜底，靠 Shape+Pattern+颜色区分（R6）。
            ["org_lensbeam"] = new OrganelleDef("org_lensbeam", "晶状束", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/lensbeam", () => new CompositeModule("lensbeam_attack", "晶状束攻击",
                    new BallisticsModule(speed: 1f, lifetime: 0.35f), new TickModule(10f),
                    new Actuator(shape: "Beam", pattern: AttackPattern.Beam)),
                isCarrier: true, attackMethod: true, attackFamily: "Beam",
                description: "攻击方式：一条持续细束，扫过伤害。"),
            ["org_enzyme"] = new OrganelleDef("org_enzyme", "酶雾腺", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/enzyme", () => new CompositeModule("enzyme_attack", "酶雾腺攻击",
                    new LingerModule(4f), new TickModule(1f),
                    new Actuator(shape: "Field", pattern: AttackPattern.Pool)),
                isCarrier: true, attackMethod: true, attackFamily: "Pool",
                description: "攻击方式：把酶雾扔到落点，地面持续腐蚀。"),
            ["org_osmotic"] = new OrganelleDef("org_osmotic", "渗透压场", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/osmotic", () => new CompositeModule("osmotic_attack", "渗透压场攻击",
                    new AuraModule(3f), new Actuator(shape: "Field", pattern: AttackPattern.Aura)),
                isCarrier: true, attackMethod: true, attackFamily: "Aura",
                description: "攻击方式：身体周围一圈压差，进圈就伤。"),
            ["org_orbitcilia"] = new OrganelleDef("org_orbitcilia", "纤毛环带", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/orbitcilia", () => new CompositeModule("orbitcilia_attack", "纤毛环带攻击",
                    new OrbitSpin(180f), new Scatterer(2), new Actuator(pattern: AttackPattern.Orbit)),
                isCarrier: true, attackMethod: true, attackFamily: "Orbit",
                description: "攻击方式：两枚体绕身旋转打人（圣经感）。"),
            // SummonId 复用现有 Luban BehaviorArchetype 行，避免新加内容表：13=孢子仆从索敌（跟随+近战，
            // 最接近"跟随近战"）；15=菌丝体固着（R6 锁定，供 org_mycelium 沿用）。
            ["org_bud"] = new OrganelleDef("org_bud", "芽殖体", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/bud", () => new CompositeModule("bud_attack", "芽殖体攻击",
                    new SummonModule(summonId: 13, count: 1), new Actuator(shape: "Spore", pattern: AttackPattern.SummonFollow)),
                isCarrier: true, attackMethod: true, attackFamily: "SummonFollow",
                description: "攻击方式：长出一个跟随的芽体帮你撞/咬（CATALOG-v3 §A 吸收旧孢子云/噬菌体召唤语义，" +
                    "游荡/追爆差异改由基因表达，Summon archetype 仍可用）。"),
            ["org_mycelium"] = new OrganelleDef("org_mycelium", "菌丝锚", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/mycelium", () => new CompositeModule("mycelium_attack", "菌丝锚攻击",
                    new SummonModule(summonId: 15, count: 1), new Actuator(shape: "Spore", pattern: AttackPattern.SummonAnchor)),
                isCarrier: true, attackMethod: true, attackFamily: "SummonAnchor",
                description: "攻击方式：钉一根菌丝炮台，定点打附近。"),
            // ── organ-gene-rebalance-v3 story-002（CATALOG-v3 §C）：以下 12 条退役，特性迁 gene_*，
            // 不删 _defs 条目（IdSmokeReport 等既有探针按 id 遍历，删条目会静默少测）；
            // IsRetired=true + AttackMethod=false 双落：CarrierCompiler 遇 AttackMethod=false 链尾直接
            // 返回 0 HitEvent，CellLubanLoader.IsRetiredOrUnknownContent 同步把指向这些 id 的卡挡在抽卡/图鉴外。
            ["org_taxis"] = new OrganelleDef("org_taxis", "趋化索", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/taxis", () => new CompositeModule("taxis_attack", "趋化索攻击",
                    new HomingModule(0.9f), new Actuator(pattern: AttackPattern.Projectile)),
                isRetired: true,
                description: "已退役，效果迁 org_emitter + gene_taxis（CATALOG-v3 §C）。"),
            ["org_boomer"] = new OrganelleDef("org_boomer", "回旋荚", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/boomer", () => new CompositeModule("boomer_attack", "回旋荚攻击",
                    new ReturnModule(), new Actuator(shape: "Arc", pattern: AttackPattern.Boomerang)),
                isRetired: true,
                description: "已退役，效果迁 org_emitter + gene_return（CATALOG-v3 §C）。"),
            ["org_calcium"] = new OrganelleDef("org_calcium", "钙波环", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/calcium", () => new CompositeModule("calcium_attack", "钙波环攻击",
                    new Grow(1.5f), new Actuator(shape: "Wave", pattern: AttackPattern.Wave)),
                isRetired: true,
                description: "已退役，几何身份并入 org_wave；效果迁 org_wave + gene_ripple + gene_swell（CATALOG-v3 §C）。"),

            // ── combat-identity-rework story-006：CATALOG §A2 第二波 12 个新攻击方式 ──
            // 不新增美术：ArtId 未登记进 SimVisualLibrary，落 SphereUnit() 兜底，靠 Shape+Pattern+字段区分（同 003 先例）。
            ["org_needle"] = new OrganelleDef("org_needle", "骨针", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/needle", () => new CompositeModule("needle_attack", "骨针攻击",
                    new BallisticsModule(speed: 1.4f), new PierceModule(2), new Actuator(pattern: AttackPattern.Projectile)),
                isRetired: true,
                description: "已退役，效果迁 org_emitter + gene_tubule（CATALOG-v3 §C）。"),
            ["org_acidgland"] = new OrganelleDef("org_acidgland", "酸腺", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/acidgland", () => new CompositeModule("acidgland_attack", "酸腺攻击",
                    new BallisticsModule(speed: 0.9f, gravity: 1.2f), new LingerModule(3f), new TickModule(1f),
                    new Actuator(shape: "Arc", pattern: AttackPattern.Pool)),
                isRetired: true,
                description: "已退役，效果迁 org_enzyme + gene_arc + gene_acidfilm（CATALOG-v3 §C）。"),
            ["org_shotgun"] = new OrganelleDef("org_shotgun", "胞吐霰", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/shotgun", () => new CompositeModule("shotgun_attack", "胞吐霰攻击",
                    new Scatterer(5), new SpreadModule(50f), new BallisticsModule(speed: 1.3f, lifetime: 0.2f),
                    new Actuator(pattern: AttackPattern.Cone)),
                isRetired: true,
                description: "已退役，效果迁 org_emitter + gene_spindle + gene_fan（CATALOG-v3 §C）。"),
            ["org_pseudopod"] = new OrganelleDef("org_pseudopod", "伪足拍", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/pseudopod", () => new CompositeModule("pseudopod_attack", "伪足拍攻击",
                    new SpreadModule(70f), new KnockbackModule(4f), new Actuator(shape: "Melee", pattern: AttackPattern.Cone)),
                isCarrier: true, attackMethod: true, attackFamily: "Cone",
                description: "攻击方式：身前宽拍并击退。"),
            ["org_hook"] = new OrganelleDef("org_hook", "钩足", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/hook", () => new CompositeModule("hook_attack", "钩足攻击",
                    new ReturnModule(), new Actuator(shape: "Arc", pattern: AttackPattern.Melee)),
                isRetired: true,
                description: "已退役，效果迁 org_cilia + gene_return（CATALOG-v3 §C）。"),
            ["org_synapsearc"] = new OrganelleDef("org_synapsearc", "放电突触", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/synapsearc", () => new CompositeModule("synapsearc_attack", "放电突触攻击",
                    new ChainModule(3), new Actuator(shape: "Arc", pattern: AttackPattern.Chain)),
                isRetired: true,
                description: "已退役，效果迁 org_emitter + gene_volt（CATALOG-v3 §C）。"),
            // SummonId 直接=BehaviorArchetype 表 id（MetabolicSliceBridge.ApplySummon）：本机 Windows
            // Smart App Control（今日新启用，见证据 md）拦截 dotnet 加载 Luban.dll，本 story 无法跑
            // Luban codegen，故不新增专属 BehaviorArchetype 行——org_spore 复用 13 号「孢子仆从索敌」
            // （org_bud 同款，均 MinionSeekAttack/会动会追），CATALOG 只要求「芽体/孢子/噬菌体与固着
            // 可区分」，不要求三者互相区分，14=噬菌体追爆（002 已预留的 MinionSeekExplode 行，本
            // story 首次消费）。
            ["org_spore"] = new OrganelleDef("org_spore", "孢子云", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/spore", () => new CompositeModule("spore_attack", "孢子云攻击",
                    new SummonModule(summonId: 13, count: 1), new Actuator(shape: "Spore", pattern: AttackPattern.SummonFollow)),
                isRetired: true,
                description: "已退役，召唤语义并入 org_bud；效果迁 org_bud + gene_drift（CATALOG-v3 §C）。"),
            ["org_phage"] = new OrganelleDef("org_phage", "噬菌体", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/phage", () => new CompositeModule("phage_attack", "噬菌体攻击",
                    new SummonModule(summonId: 14, count: 1), new Actuator(shape: "Spore", pattern: AttackPattern.SummonFollow)),
                isRetired: true,
                description: "已退役，召唤语义并入 org_bud；效果迁 org_bud + gene_apoptosis + gene_taxis（CATALOG-v3 §C）。"),
            ["org_drill"] = new OrganelleDef("org_drill", "纤毛钻", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/drill", () => new CompositeModule("drill_attack", "纤毛钻攻击",
                    new BallisticsModule(speed: 2.2f, lifetime: 0.25f), new PierceModule(2),
                    new Actuator(shape: "Melee", pattern: AttackPattern.Dash)),
                isCarrier: true, attackMethod: true, attackFamily: "Dash",
                description: "攻击方式：短冲刺，路径上穿刺。"),
            // organ-gene-rebalance-v3 story-002（CATALOG-v3 §A）：org_wave 改名「波形器」，合并旧
            // org_calcium（钙波环）+ 旧 org_wave（胞质浪）的几何身份；Grow 幅度取二者上限（1.5f），
            // 180° 弧默认可被基因改（gene_ripple/gene_fan 等，005/006 接线）。
            ["org_wave"] = new OrganelleDef("org_wave", "波形器", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/wave", () => new CompositeModule("wave_attack", "波形器攻击",
                    new SpreadModule(180f), new Grow(1.5f), new Actuator(shape: "Wave", pattern: AttackPattern.Wave)),
                isCarrier: true, attackMethod: true, attackFamily: "Wave",
                description: "攻击方式：朝面向打出可扩散的新月波（合并旧钙波环+胞质浪几何身份，180° 弧默认可被基因改）。"),
            ["org_trail"] = new OrganelleDef("org_trail", "粘液腺", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/trail", () => new CompositeModule("trail_attack", "粘液腺攻击",
                    new TrailModule(2f), new Actuator(shape: "Field", pattern: AttackPattern.Pool)),
                isRetired: true,
                description: "已退役，效果迁任意底盘 + gene_slime（移动拖尾走既有攻击几何，不新增玩家位移采样，" +
                    "见 preflight R10，CATALOG-v3 §C）。"),
            ["org_pulse"] = new OrganelleDef("org_pulse", "节律鼓", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/pulse", () => new CompositeModule("pulse_attack", "节律鼓攻击",
                    new AuraModule(2f), new TickModule(0.5f), new Actuator(shape: "Wave", pattern: AttackPattern.Pulse)),
                isRetired: true,
                description: "已退役，效果迁 org_osmotic + gene_rhythm（CATALOG-v3 §C）。"),

            // ── 人直接指示追加（2026-08-25，非 sprint-027 范围）：吞噬（接触消灭小于自身体积的目标）
            // 从"全局无条件行为"改为需要装备并激活的器官——见 CellDevourSystem.OnUpdate 的门控。
            ["org_phago"] = new OrganelleDef("org_phago", "吞噬体", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/phago", () => new CompositeModule("phago_attack", "吞噬体接触",
                    new Actuator(shape: "Melee", pattern: AttackPattern.Thorns)),
                isCarrier: true, attackMethod: true, attackFamily: "Thorns",
                description: "攻击方式：接触到体积小于自己的目标时直接吞噬消灭；不主动开火。"),

            // ── organelle-structural-tier story-001（DESIGN §2/§3、CATALOG §A）：首批 8 条结构器官，
            // 常驻被动叠加，不进攻击链（IsCarrier=false/AttackMethod=false/AllowedSlotTypes=null），
            // v1 不分配 CarrierInstance/Slots，不开基因槽。
            ["org_carapace"] = new OrganelleDef("org_carapace", "甲壳", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/carapace", null, category: OrganelleCategory.Structural,
                structuralEffects: new[] { new StatModifier(StatId.DamageTaken, ModifierOp.PctAdd, -0.12f) },
                description: "常驻被动：降低受到伤害。"),
            ["org_flagellum_boost"] = new OrganelleDef("org_flagellum_boost", "鞭毛强化", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/flagellum_boost", null, category: OrganelleCategory.Structural,
                structuralEffects: new[] { new StatModifier(StatId.MoveSpeed, ModifierOp.PctAdd, 0.12f) },
                description: "常驻被动：提升移动速度。"),
            ["org_thick_membrane"] = new OrganelleDef("org_thick_membrane", "厚膜", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/thick_membrane", null, category: OrganelleCategory.Structural,
                structuralEffects: new[] { new StatModifier(StatId.MaxHealth, ModifierOp.Flat, 32f) },
                description: "常驻被动：提升生命上限。"),
            ["org_regen_gland"] = new OrganelleDef("org_regen_gland", "再生腺", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/regen_gland", null, category: OrganelleCategory.Structural,
                structuralEffects: new[] { new StatModifier(StatId.HealthRegen, ModifierOp.Flat, 0.8f) },
                description: "常驻被动：提升生命回复。"),
            ["org_chemoreceptor"] = new OrganelleDef("org_chemoreceptor", "化学受体", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/chemoreceptor", null, category: OrganelleCategory.Structural,
                structuralEffects: new[] { new StatModifier(StatId.PickupRadius, ModifierOp.PctAdd, 0.30f) },
                description: "常驻被动：扩大拾取半径。"),
            ["org_efficient_gut"] = new OrganelleDef("org_efficient_gut", "高效消化道", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/efficient_gut", null, category: OrganelleCategory.Structural,
                structuralEffects: new[] { new StatModifier(StatId.NutrientGain, ModifierOp.PctAdd, 0.18f) },
                description: "常驻被动：提升营养质获取。"),
            ["org_calm_membrane"] = new OrganelleDef("org_calm_membrane", "镇静膜", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/calm_membrane", null, category: OrganelleCategory.Structural,
                structuralEffects: new[] { new StatModifier(StatId.AggroScale, ModifierOp.PctAdd, -0.20f) },
                description: "常驻被动：降低敌人仇恨。"),
            ["org_stamina_sac"] = new OrganelleDef("org_stamina_sac", "耐力囊", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/stamina_sac", null, category: OrganelleCategory.Structural,
                structuralEffects: new[]
                {
                    new StatModifier(StatId.StaminaMax, ModifierOp.Flat, 25f),
                    new StatModifier(StatId.StaminaRegen, ModifierOp.PctAdd, 0.15f),
                },
                description: "常驻被动：提升耐力上限与回复。"),
        };

        public static OrganelleDef Get(string id) => _defs.TryGetValue(id, out var def) ? def : null;

        public static IReadOnlyDictionary<string, OrganelleDef> All => _defs;
    }
}
