using System.Collections.Generic;

namespace GameLogic.Core
{
    /// <summary>
    /// M4-R00-02 队列④-17（FS-REQ-030）：共享能力封闭清单，定义见
    /// `DesignDocs/detailed/01_Faction_Survival_And_Ecology.md` 第127~145行。13 项之前只存在于
    /// 设计文档表格里，代码里没有任何一处能查到"这13项分别谁实现了、实现到什么程度"。
    /// </summary>
    public enum SharedCapability
    {
        Perception,
        MovementPathfinding,
        Attack,
        Harvest,
        Carry,
        FeedSustain,
        Store,
        Repair,
        ReproduceIncubate,
        GuardEscort,
        Retreat,
        SampleAnalyze,
        Signal,
    }

    /// <summary>FS-REQ-030 验收粒度：需求表格「输入/输出/失败原因」三列是否都有对应真实代码路径。</summary>
    public enum SharedCapabilityStatus
    {
        /// <summary>全仓查无任何对应实现，含占位字段。</summary>
        NotImplemented,

        /// <summary>有类型/枚举/字段占位，但没有真正的输入→输出→失败路径消费它。</summary>
        Placeholder,

        /// <summary>有真实逻辑，但输入/输出/失败三列缺至少一列，或服务的资源域/入口与需求定义不符。</summary>
        Partial,

        /// <summary>输入/输出/失败三列都有对应代码路径，且未见「阵营专属复制一套逻辑」的分支（FS-REQ-031）。</summary>
        Implemented,
    }

    /// <summary>一条能力的核实结果，<see cref="Evidence"/> 只是给人看的一句话摘要（含 file:line），
    /// 不参与任何运行时逻辑——真正的判断依据永远是代码本身，这份清单只是把判断结果显式记下来，
    /// 不许让它和代码脱节（改动某项能力后必须同步改这里）。</summary>
    public readonly struct SharedCapabilityEntry
    {
        public readonly SharedCapability Capability;
        public readonly SharedCapabilityStatus Status;
        public readonly string Evidence;

        public SharedCapabilityEntry(SharedCapability capability, SharedCapabilityStatus status, string evidence)
        {
            Capability = capability;
            Status = status;
            Evidence = evidence;
        }
    }

    /// <summary>
    /// 2026-09-16 核实快照（子 agent 独立复核，未采信旧审计
    /// `FS_REQ_M1_M4_Blocking_Audit.md`/`05_M1_M4_Backfill_And_Acceptance.md` 的"已落地5项"结论，
    /// 逐项重新核实后有 3 项状态被推翻或降级，见下方各条注释）。放在 <c>GameLogic.Core</c> 而不是
    /// 任一玩法子系统（<c>MetabolicSlice</c>/<c>Command.Formation</c>/<c>Control</c>），是因为 13
    /// 项能力横跨这些命名空间，归属任何单一子系统都会造成"这个 topic 不该属于它"的耦合——这里和
    /// <see cref="GameSignals"/> 一样，是热更层的跨系统契约声明，不是内核实现。
    ///
    /// 这份清单同时是 `.claude/rules/projecta-spec-completeness.md` 要求的"需求ID→生产入口→代码→
    /// 自动测试→玩家反馈"矩阵里，"生产入口"一列的单一权威来源——不要在别处重复维护一份判断。
    /// </summary>
    public static class SharedCapabilityCatalog
    {
        public static readonly IReadOnlyList<SharedCapabilityEntry> Entries = new[]
        {
            new SharedCapabilityEntry(SharedCapability.Perception, SharedCapabilityStatus.Partial,
                "MinionTargetingUtil.TryFindNearestHostile / MetabolicSliceBridge.FindNearestHostile：" +
                "只有最近目标查询，无置信度输出，无遮挡/干扰建模，未区分超距/遮挡/干扰三种失败原因"),

            // 2026-09-16 由"已实现"降级为"部分实现"：只有转向行为，无真实寻路，"不可达"这条
            // 失败原因没有对应判定（旧审计未区分"卡死"与"不可达"是两件事）。
            new SharedCapabilityEntry(SharedCapability.MovementPathfinding, SharedCapabilityStatus.Partial,
                "JobSteering.Steer + FormationCommandFailReason(Stuck/NoValidAnchor)：" +
                "只有空间哈希+转向行为，无 NavMesh/路点寻路，'不可达'失败原因未实现"),

            new SharedCapabilityEntry(SharedCapability.Attack, SharedCapabilityStatus.Implemented,
                "JobDamage.Execute/TryDamage/IsValidTarget/JobContactDamage：单一共享 Job，" +
                "阵营只是过滤字段不是复制分支，输入/输出/失败三列齐全"),

            new SharedCapabilityEntry(SharedCapability.Harvest, SharedCapabilityStatus.Partial,
                "WildOrganRegistry.TryPickup：三列结构齐全，但服务对象是野生器官战利品，" +
                "不是 FS-REQ-020 定义的食源接口，且瞬时完成、无'时间'维度"),

            new SharedCapabilityEntry(SharedCapability.Carry, SharedCapabilityStatus.Placeholder,
                "FormationCommand.CommandKind.Carry：仅有枚举值，Formation 目录内对它的 case 分支/" +
                "调用点为 0，没有分配容量成员/拾取/运输/交付的任何实现"),

            new SharedCapabilityEntry(SharedCapability.FeedSustain, SharedCapabilityStatus.NotImplemented,
                "全仓查无 Feed/Nourish/供养/LocalReserve 对应实现，依赖的食源对象体系本身也不存在"),

            new SharedCapabilityEntry(SharedCapability.Store, SharedCapabilityStatus.Partial,
                "BiomassLedger（资源余额记账，无容量上限/断网/仓毁失败）+ BagInventory（容量满会失败，" +
                "但服务对象是玩家个人器官槽，不是仓/巢网络存储）：两套各占一半，域都不对口"),

            // 2026-09-16 推翻旧审计"已落地5项"里的判断：全仓查无"伤势→物质/供能→完整度/器官状态
            // 恢复"的实现路径。UnitVitals.MetabolismRegenPerSecond 是代谢资源自然回复，与"器官修复"
            // 语义不同——被摧毁的身体接点没有任何写回"未摧毁"的代码路径。
            new SharedCapabilityEntry(SharedCapability.Repair, SharedCapabilityStatus.NotImplemented,
                "全仓查无 RestorePart/RepairPart 或等价实现；SurgicalRewardLedger 只读 Destroyed 状态" +
                "做奖励判定，从未写回；UnitVitals.MetabolismRegenPerSecond 是代谢资源回复非器官修复"),

            new SharedCapabilityEntry(SharedCapability.ReproduceIncubate, SharedCapabilityStatus.Implemented,
                "GerminationChamberRegistry.Enqueue：输入(模板+生物质)/输出(定时后生成新生个体)/" +
                "失败(生物质不足/腔体已毁/模板缺失)三列齐全"),

            // 2026-09-16 由"已实现"降级为"部分实现"：玩家侧 Guard 命令的 TargetEntity 目前恒为
            // None（SquadCommandSystem 只支持对点位下 Guard），"保护目标"这个输入没有从玩家入口
            // 真正接上，只有"占点位"和 Escort 教义的目标评分两个碎片，未见完整的阵位/拦截/撤离状态机。
            new SharedCapabilityEntry(SharedCapability.GuardEscort, SharedCapabilityStatus.Partial,
                "SquadCommandSystem.Issue(Guard,...)：TargetEntity 恒为 SimEntityId.None；" +
                "FormationDoctrineProfile.Escort 教义只影响目标评分，无完整阵位/拦截/撤离状态机"),

            new SharedCapabilityEntry(SharedCapability.Retreat, SharedCapabilityStatus.Implemented,
                "JobCommandIntent(Retreat) + FormationDoctrineProfile.RetreatHealthThreshold：" +
                "与 AI 受惊 BehaviorKind.Flee 共用同一条内核转向逻辑，无阵营分支"),

            new SharedCapabilityEntry(SharedCapability.SampleAnalyze, SharedCapabilityStatus.Implemented,
                "WildOrganRegistry.TryResolveAtChamber：输入(证据+处理节点)/输出(调用 blueprints.Resolve)/" +
                "失败(样本丢失/未携带/离腔体太远)三列齐全"),

            // 2026-09-16 推翻旧审计默认判断：现有"信号范围"服务的是"玩家换控单位"，不是
            // "消息/命令/知识在网络间传播"；DefaultControlSignalRange 被拉到百万单位=事实上永不
            // 失效，GerminationChamberRegistry.IsNetworked 恒真，注释自陈是占位。
            new SharedCapabilityEntry(SharedCapability.Signal, SharedCapabilityStatus.Placeholder,
                "SimBridge.DefaultControlSignalRange=1_000_000f（事实上永不失效，且服务对象是换控" +
                "而非命令/知识传播）+ GerminationChamberRegistry.IsNetworked 恒真占位"),
        };

        public static SharedCapabilityStatus StatusOf(SharedCapability capability)
        {
            foreach (SharedCapabilityEntry entry in Entries)
            {
                if (entry.Capability == capability)
                {
                    return entry.Status;
                }
            }

            return SharedCapabilityStatus.NotImplemented;
        }
    }
}
