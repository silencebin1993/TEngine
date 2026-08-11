using System;
using System.Collections.Generic;
using ChemEngine.Builtin.Modules;
using ChemEngine.Core;
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
        public OrganelleRole Role { get; }
        public OrganelleAttachTarget AttachTarget { get; }

        /// <summary>null = 不限地板（AttachTarget=Slot 时生效；DirectedEdge 恒 null）。</summary>
        public HashSet<SlotType> AllowedSlotTypes { get; }

        public string ArtId { get; }
        public Func<IModule> CreateModule { get; }

        public OrganelleDef(string id, string displayName, OrganelleRole role, OrganelleAttachTarget attachTarget,
            IEnumerable<SlotType> allowedSlotTypes, string artId, Func<IModule> createModule)
        {
            Id = id;
            DisplayName = displayName;
            Role = role;
            AttachTarget = attachTarget;
            AllowedSlotTypes = allowedSlotTypes == null ? null : new HashSet<SlotType>(allowedSlotTypes);
            ArtId = artId;
            CreateModule = createModule;
        }
    }

    /// <summary>
    /// 冻结总案 §5.2 v1 器官目录（24 条）。17 条复用既有 ChemEngine Module（构造参数不同）；
    /// 7 条 CreateModule=null（多入合并拓扑 / 受击出口协议 / 邻格催化半径 / DirectedEdge 挂载执行管线均未开），
    /// 理由逐条见冻结决策 N1，本文件只负责注册元数据，不额外建状态机。
    /// </summary>
    public static class OrganelleCatalog
    {
        private static readonly SlotType[] MembraneOnly = { SlotType.Membrane };

        private static readonly Dictionary<string, OrganelleDef> _defs = new Dictionary<string, OrganelleDef>
        {
            ["org_mito"] = new OrganelleDef("org_mito", "线粒体", OrganelleRole.Source, OrganelleAttachTarget.Slot,
                null, "org/mito", () => new EnergyCore()),
            ["org_chloro"] = new OrganelleDef("org_chloro", "叶绿体", OrganelleRole.Source, OrganelleAttachTarget.Slot,
                null, "org/chloro", () => new EnergyCore()),
            ["org_vacuole"] = new OrganelleDef("org_vacuole", "液泡电容", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/vacuole", () => new Capacitor()),
            ["org_golgi"] = new OrganelleDef("org_golgi", "高尔基分流", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/golgi", () => new Splitter()),
            ["org_merge"] = new OrganelleDef("org_merge", "囊泡汇流", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/merge", null),
            ["org_lens"] = new OrganelleDef("org_lens", "晶状聚焦", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/lens", () => new FocusLens()),
            ["org_scatter"] = new OrganelleDef("org_scatter", "纺锤散射", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/scatter", () => new Scatterer()),
            ["org_swell"] = new OrganelleDef("org_swell", "膨胀泡", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/swell", () => new Grow()),
            ["org_flagella"] = new OrganelleDef("org_flagella", "鞭毛环", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/flagella", () => new OrbitSpin()),
            ["org_lyso"] = new OrganelleDef("org_lyso", "溶酶爆", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/lyso", () => new ExplodeOnHit()),
            ["org_perox"] = new OrganelleDef("org_perox", "过氧化物酶", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/perox", () => new TagAttach("Fire")),
            ["org_aqua"] = new OrganelleDef("org_aqua", "水合泡", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/aqua", () => new TagAttach("Wet")),
            ["org_ion"] = new OrganelleDef("org_ion", "离子泵", OrganelleRole.Transform, OrganelleAttachTarget.Slot,
                null, "org/ion", () => new TagAttach("Shock")),
            ["org_radiator"] = new OrganelleDef("org_radiator", "散热褶", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/radiator", () => new HeatSink()),
            ["org_breaker"] = new OrganelleDef("org_breaker", "热休克闸", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/breaker", () => new Fuse()),
            ["org_synapse"] = new OrganelleDef("org_synapse", "突触反馈", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                null, "org/synapse", () => new FeedbackLoop(FeedbackMode.Hit)),
            ["org_emitter"] = new OrganelleDef("org_emitter", "分泌喷射器", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/emitter", () => new Actuator()),
            ["org_cilia"] = new OrganelleDef("org_cilia", "纤毛刺", OrganelleRole.Sink, OrganelleAttachTarget.Slot,
                null, "org/cilia", () => new Actuator()),
            ["org_spine"] = new OrganelleDef("org_spine", "刺突", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/spine", null),
            ["org_slime"] = new OrganelleDef("org_slime", "粘液层", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/slime", null),
            ["org_receptor"] = new OrganelleDef("org_receptor", "受体", OrganelleRole.Relay, OrganelleAttachTarget.Slot,
                MembraneOnly, "org/receptor", null),
            ["org_insulate"] = new OrganelleDef("org_insulate", "绝缘管", OrganelleRole.Edge, OrganelleAttachTarget.DirectedEdge,
                null, "org/insulate", null),
            ["org_valve"] = new OrganelleDef("org_valve", "单向阀", OrganelleRole.Edge, OrganelleAttachTarget.DirectedEdge,
                null, "org/valve", null),
            ["org_filter"] = new OrganelleDef("org_filter", "过滤管", OrganelleRole.Edge, OrganelleAttachTarget.DirectedEdge,
                null, "org/filter", null),
        };

        public static OrganelleDef Get(string id) => _defs.TryGetValue(id, out var def) ? def : null;

        public static IReadOnlyDictionary<string, OrganelleDef> All => _defs;
    }
}
