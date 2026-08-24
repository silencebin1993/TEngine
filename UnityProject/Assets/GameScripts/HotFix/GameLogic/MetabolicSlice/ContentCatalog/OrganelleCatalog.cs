using System;
using System.Collections.Generic;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    public enum OrganelleRole { Source, Relay, Transform, Sink, Edge }

    public enum OrganelleAttachTarget { Slot, DirectedEdge }

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

        public OrganelleDef(string id, string displayName, OrganelleRole role, OrganelleAttachTarget attachTarget,
            IEnumerable<SlotType> allowedSlotTypes, string artId, Func<IModule> createModule, bool isCarrier = false,
            bool isRetired = false, string description = "", bool attackMethod = false)
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
            ["org_vacuole"] = new OrganelleDef("org_vacuole", "液泡电容", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/vacuole", () => new Capacitor(), isCarrier: true,
                description: "按倍率放大流经的能量。"),
            ["org_golgi"] = new OrganelleDef("org_golgi", "高尔基分流", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/golgi", () => new Splitter(), isCarrier: true,
                description: "把能量按支路数均分，供后续模块或多路执行器使用。"),
            ["org_merge"] = new OrganelleDef("org_merge", "囊泡汇流", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/merge", () => new MergeInlet(), isCarrier: true,
                description: "把入流能量夹到带宽上限，超出部分折半转为热量。"),
            ["org_lens"] = new OrganelleDef("org_lens", "晶状聚焦", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/lens", () => new FocusLens(), isCarrier: true,
                description: "提升伤害的同时必定增加热量。"),
            ["org_scatter"] = new OrganelleDef("org_scatter", "纺锤散射", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/scatter", () => new Scatterer(), isCarrier: true,
                description: "按强度倍率增加命中次数（分裂/多段）。"),
            ["org_swell"] = new OrganelleDef("org_swell", "膨胀泡", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/swell", () => new Grow(), isCarrier: true,
                description: "弹体尺度随强度倍率变大，命中范围同步放大。"),
            ["org_flagella"] = new OrganelleDef("org_flagella", "鞭毛环", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/flagella", () => new OrbitSpin(), isCarrier: true,
                description: "赋予弹体自旋角速度，改变弹道/绕轨轨迹。"),
            ["org_lyso"] = new OrganelleDef("org_lyso", "溶酶爆", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/lyso", () => new ExplodeOnHit(), isCarrier: true,
                description: "命中后触发爆炸效果。"),
            ["org_perox"] = new OrganelleDef("org_perox", "过氧化物酶", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/perox", () => new TagAttach("Fire"), isCarrier: true,
                description: "命中附加火焰标记。"),
            ["org_aqua"] = new OrganelleDef("org_aqua", "水合泡", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/aqua", () => new TagAttach("Wet"), isCarrier: true,
                description: "命中附加潮湿标记。"),
            ["org_ion"] = new OrganelleDef("org_ion", "离子泵", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/ion", () => new TagAttach("Shock"), isCarrier: true,
                description: "命中附加感电标记。"),
            ["org_radiator"] = new OrganelleDef("org_radiator", "散热褶", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/radiator", () => new HeatSink(), isCarrier: true,
                description: "持续压低当前热量，不低于 0。"),
            ["org_breaker"] = new OrganelleDef("org_breaker", "热休克闸", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/breaker", () => new Fuse(), isCarrier: true,
                description: "能量超过安全上限时跳闸夹住，防止后续模块继续放大溢出。"),
            ["org_synapse"] = new OrganelleDef("org_synapse", "突触反馈", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/synapse", () => new FeedbackLoop(FeedbackMode.Hit), isCarrier: true,
                description: "把命中能量的一部分回灌自身，按预算封顶防止无限自激。"),
            ["org_emitter"] = new OrganelleDef("org_emitter", "分泌喷射器", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/emitter", () => new CompositeModule("emitter_attack", "分泌喷射器攻击",
                    new BallisticsModule(speed: 1.3f), new Actuator(pattern: AttackPattern.Projectile)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：朝瞄准方向射出代谢弹。"),
            ["org_cilia"] = new OrganelleDef("org_cilia", "纤毛刺", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/cilia", () => new CompositeModule("cilia_attack", "纤毛刺攻击",
                    new SpreadModule(40f), new Actuator(shape: "Melee", pattern: AttackPattern.Melee)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：身前短锥挥刺。"),
            ["org_spine"] = new OrganelleDef("org_spine", "刺突", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/spine", () => new CompositeModule("spine_attack", "刺突反击",
                    new Thorns(reflectDamage: 6f), new Actuator(shape: "Melee", pattern: AttackPattern.Thorns)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：被碰到或挨打时反刺，不靠主动开火。"),
            ["org_slime"] = new OrganelleDef("org_slime", "粘液层", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/slime", () => new TagAttach("Oil"), isCarrier: true,
                description: "命中附加油污标记。"),
            ["org_receptor"] = new OrganelleDef("org_receptor", "受体", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/receptor", () => new TagAttach("Catalyst"), isCarrier: true,
                description: "命中附加催化标记。"),
            ["org_insulate"] = new OrganelleDef("org_insulate", "绝缘管", OrganelleRole.Edge, OrganelleAttachTarget.DirectedEdge,
                null, "org/insulate", () => new Insulator(), isRetired: true,
                description: "按比例阻尼热量（已退役，不进可装备插槽）。"),
            ["org_valve"] = new OrganelleDef("org_valve", "单向阀", OrganelleRole.Edge, OrganelleAttachTarget.Slot,
                null, "org/valve", () => new Valve(), isCarrier: true,
                description: "强化沿链方向流动的能量。"),
            ["org_filter"] = new OrganelleDef("org_filter", "过滤管", OrganelleRole.Edge, OrganelleAttachTarget.Slot,
                null, "org/filter", () => new TagFilter("Approved", 0.5f), isCarrier: true,
                description: "只放行带指定标记的能量，未命中的按比例折损。"),

            // ── combat-identity-rework story-003：CATALOG §A1 第一波 9 个新攻击方式 ──
            // 不新增美术：ArtId 未登记进 SimVisualLibrary，落 SphereUnit() 兜底，靠 Shape+Pattern+颜色区分（R6）。
            ["org_lensbeam"] = new OrganelleDef("org_lensbeam", "晶状束", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/lensbeam", () => new CompositeModule("lensbeam_attack", "晶状束攻击",
                    new BallisticsModule(speed: 1f, lifetime: 0.35f), new TickModule(10f),
                    new Actuator(shape: "Beam", pattern: AttackPattern.Beam)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：一条持续细束，扫过伤害。"),
            ["org_enzyme"] = new OrganelleDef("org_enzyme", "酶雾腺", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/enzyme", () => new CompositeModule("enzyme_attack", "酶雾腺攻击",
                    new LingerModule(4f), new TickModule(1f),
                    new Actuator(shape: "Field", pattern: AttackPattern.Pool)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：把酶雾扔到落点，地面持续腐蚀。"),
            ["org_osmotic"] = new OrganelleDef("org_osmotic", "渗透压场", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/osmotic", () => new CompositeModule("osmotic_attack", "渗透压场攻击",
                    new AuraModule(3f), new Actuator(shape: "Field", pattern: AttackPattern.Aura)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：身体周围一圈压差，进圈就伤。"),
            ["org_orbitcilia"] = new OrganelleDef("org_orbitcilia", "纤毛环带", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/orbitcilia", () => new CompositeModule("orbitcilia_attack", "纤毛环带攻击",
                    new OrbitSpin(180f), new Scatterer(2), new Actuator(pattern: AttackPattern.Orbit)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：两枚体绕身旋转打人（圣经感）。"),
            // SummonId 复用现有 Luban BehaviorArchetype 行，避免新加内容表：13=孢子仆从索敌（跟随+近战，
            // 最接近"跟随近战"）；15=菌丝体固着（R6 锁定，供 org_mycelium 沿用）。
            ["org_bud"] = new OrganelleDef("org_bud", "芽殖体", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/bud", () => new CompositeModule("bud_attack", "芽殖体攻击",
                    new SummonModule(summonId: 13, count: 1), new Actuator(shape: "Spore", pattern: AttackPattern.SummonFollow)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：长出一个跟随的芽体帮你撞/咬。"),
            ["org_mycelium"] = new OrganelleDef("org_mycelium", "菌丝锚", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/mycelium", () => new CompositeModule("mycelium_attack", "菌丝锚攻击",
                    new SummonModule(summonId: 15, count: 1), new Actuator(shape: "Spore", pattern: AttackPattern.SummonAnchor)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：钉一根菌丝炮台，定点打附近。"),
            ["org_taxis"] = new OrganelleDef("org_taxis", "趋化索", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/taxis", () => new CompositeModule("taxis_attack", "趋化索攻击",
                    new HomingModule(0.9f), new Actuator(pattern: AttackPattern.Projectile)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：弹自己会拐去最近敌人。"),
            ["org_boomer"] = new OrganelleDef("org_boomer", "回旋荚", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/boomer", () => new CompositeModule("boomer_attack", "回旋荚攻击",
                    new ReturnModule(), new Actuator(shape: "Arc", pattern: AttackPattern.Boomerang)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：荚壳飞出再飞回，来回都打。"),
            ["org_calcium"] = new OrganelleDef("org_calcium", "钙波环", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/calcium", () => new CompositeModule("calcium_attack", "钙波环攻击",
                    new Grow(1.5f), new Actuator(shape: "Wave", pattern: AttackPattern.Wave)),
                isCarrier: true, attackMethod: true,
                description: "攻击方式：从自己往外扩一圈钙波。"),
        };

        public static OrganelleDef Get(string id) => _defs.TryGetValue(id, out var def) ? def : null;

        public static IReadOnlyDictionary<string, OrganelleDef> All => _defs;
    }
}
