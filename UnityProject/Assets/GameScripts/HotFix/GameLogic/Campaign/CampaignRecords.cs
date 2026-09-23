using System;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>通用"资源类型 + 数量"条目，供 MachineRecord.Cargo / BuildingRecord.Inventory 复用。
    /// 全 public 字段（非属性）以兼容 <see cref="JsonUtility"/>。</summary>
    [Serializable]
    public sealed class CargoEntry
    {
        public string ResourceType;
        public int Amount;
    }

    /// <summary>ERD-WRK-002 五类工作优先级（Haul/Build/Repair/Salvage/Recharge）。数值 1～4 越大
    /// 优先级越高，0＝该机器永久禁用该类工作（ER3-WRK-02 起自动分配的唯一读取来源，不影响玩家
    /// 直接点选下令——直控式点选是显式命令，绕过优先级偏好）。ER1-SAVE-01 只落盘骨架时字段全部
    /// 是 0；<see cref="Default"/> 是"全部启用、优先级中等"的出厂值，新机器与旧存档全零迁移都用它，
    /// 详见 <see cref="MachineRegistry"/> 的调用点。</summary>
    [Serializable]
    public sealed class WorkPriorities
    {
        public int Haul;
        public int Build;
        public int Repair;
        public int Salvage;
        public int Recharge;

        public static WorkPriorities Default() => new WorkPriorities
        {
            Haul = 2,
            Build = 2,
            Repair = 2,
            Salvage = 2,
            Recharge = 2,
        };

        /// <summary>本项目从未发布过存档兼容承诺；"全零"只可能是 ER1-SAVE-01 骨架期从未真正写过
        /// 优先级的旧战役（本 Story 起才第一次有 UI 能把某一类调回 0＝禁用），迁移时一并变成
        /// <see cref="Default"/>。真正的玩家操作不可能一次性把五类全部调成禁用之外还恰好全是 0——
        /// 但即使真的发生，后果也只是"这台机器下一次读档后自动分配恢复默认优先级"，不是数据损坏。</summary>
        public bool IsUninitialized() => Haul == 0 && Build == 0 && Repair == 0 && Salvage == 0 && Recharge == 0;

        public int Get(WorkOrderKind kind)
        {
            switch (kind)
            {
                case WorkOrderKind.Haul: return Haul;
                case WorkOrderKind.Build: return Build;
                case WorkOrderKind.Repair: return Repair;
                case WorkOrderKind.Salvage: return Salvage;
                case WorkOrderKind.Recharge: return Recharge;
                default: return 0;
            }
        }

        public void Set(WorkOrderKind kind, int value)
        {
            switch (kind)
            {
                case WorkOrderKind.Haul: Haul = value; break;
                case WorkOrderKind.Build: Build = value; break;
                case WorkOrderKind.Repair: Repair = value; break;
                case WorkOrderKind.Salvage: Salvage = value; break;
                case WorkOrderKind.Recharge: Recharge = value; break;
            }
        }

        public WorkPriorities Clone() => new WorkPriorities
        {
            Haul = Haul, Build = Build, Repair = Repair, Salvage = Salvage, Recharge = Recharge,
        };
    }

    /// <summary>ERD-DAT-002 MachineRecord：个体机器的长期真相。
    /// <c>LogicId</c> 跨进程稳定且永不复用；<c>SimEntityId</c> 只在当前 SimWorld 内有效，
    /// **绝不落盘**（本类型故意不含该字段——ER1-ID-01 会补唯一 MachineRegistry 时也遵守同一红线）。</summary>
    [Serializable]
    public sealed class MachineRecord
    {
        public int LogicId;
        public int DisplayNumber;
        public string ChassisId;
        public string BlueprintId;
        public int BlueprintVersion;
        public string LoadoutSignature;
        public string FactionId;
        public string RegionId;
        public Vector2 WorldPosition;
        public float Health;
        public float MaxHealth;
        public string[] InjuryFlags;
        public float Battery;
        public string CurrentWorkOrderId;
        public CargoEntry[] Cargo;
        public string DoctrineId;
        public WorkPriorities WorkPriorities;
        public string[] ExperienceFlags;
        public int KillCount;
        public int JobsCompleted;
        public int ExpeditionsCompleted;
        public int TimesControlled;
        public bool IsAlive;
        public bool IsInFactory;
        public bool IsDeployed;
    }

    /// <summary>ERD-DAT-003 BlueprintRecord 的单条版本，只追加不原地改写。</summary>
    [Serializable]
    public sealed class BlueprintVersionRecord
    {
        public int Version;
        public string ChassisId;
        public string PrimaryId;
        public string UtilityId;
        public string StructureId;
        public string[] OrderedFirmwareIds;
        public WorkPriorities WorkPriorityTemplate;
        public string DoctrineId;
        public int ScrapCost;
        public int PowerCost;
        public int BandwidthCost;
        public float HeatBudget;
        public string[] FactionTags;
        public string CompileSignature;
        public float CreatedAtPlaySeconds;

        // ── ER4-PRIM-02：3×3 电路板（PRIMITIVE-FULL-DEMO-SPEC.md §3）──────────────────
        // 旧档（ER1-SAVE-01～ER4-FAC-01 期间创建的骨架）这五个字段全为 null/0，
        // 由 Blueprint.BlueprintCircuitDefaults.EnsureCircuitDataSeeded 在下次进入归还谷地时
        // 就地迁入默认合法板（见该类注释），不复制任何玩家仓实例。

        /// <summary>9 槽类型，行优先 0～8，当前恒等于 <c>Blueprint.BlueprintCircuitLayout.SlotTypeAt(i)</c>
        /// 的计算结果——落盘是满足"存9槽类型"的字面存档契约与未来非固定布局的扩展点，不是每局可各自不同
        /// 的数据（Demo 固定同一网格，§3.3）。</summary>
        public GameLogic.MetabolicSlice.Grid.SlotType[] CircuitSlotTypes;

        /// <summary>9 槽内容 ID（<c>GameLogic.MetabolicSlice.CardDefs.CardCatalog</c> 的条目 id），行优先
        /// 0～8。index0＝固定源槽（<see cref="Blueprint.BlueprintCircuitChipCatalog.DefaultSourceContentId"/>
        /// 或其它 <c>IsSource</c> 条目，不可编辑——这是 DEBT-ER4PRIM01-01 的裁决落点：编译/签名/预览均真实
        /// 读取这个 ID，而不是像旧 <c>CarrierCompiler.BuildRecipe</c> 那样硬编码 <c>EnergyCore(10f)</c>）；
        /// index8＝固定汇槽，由 <see cref="PrimaryId"/> 派生（<see cref="Blueprint.BlueprintCircuitChipCatalog.ResolveSinkContentId"/>），
        /// 同样不可编辑；index1～7＝玩家可装卸的基元芯片 id，null/空串表示空槽。</summary>
        public string[] CircuitSlotContentIds;

        /// <summary>玩家画的有向导线（四邻、无自环、无重复，软帽见 <c>SlotGrid.TryAddEdge</c>），
        /// 保存前已按 (From,To) 升序排列以保证签名/JSON 稳定。</summary>
        public BlueprintCircuitEdgeRecord[] CircuitEdges;

        /// <summary>保存那一刻的 <see cref="CampaignSaveService.CurrentContentVersion"/> 快照，参与
        /// <see cref="CompileSignature"/>——未来内容表结构性变化（槽位规则、固件解析方式等）时用于识别
        /// "本版本基于已变化的内容定义编译"，触发重新校验，而不是让旧签名静默失效当作仍然合法。</summary>
        public int ContentVersionAtCompile;
    }

    /// <summary>ER4-PRIM-02：<see cref="BlueprintVersionRecord.CircuitEdges"/> 的单条有向边。
    /// 独立类型而非 (int,int) 元组是为了兼容 <see cref="JsonUtility"/>（不支持元组序列化）。</summary>
    [Serializable]
    public sealed class BlueprintCircuitEdgeRecord
    {
        public int From;
        public int To;

        public BlueprintCircuitEdgeRecord() { }

        public BlueprintCircuitEdgeRecord(int from, int to)
        {
            From = from;
            To = to;
        }
    }

    /// <summary>ERD-DAT-003 BlueprintRecord。</summary>
    [Serializable]
    public sealed class BlueprintRecord
    {
        public string BlueprintId;
        public string DisplayName;
        public int ActiveVersion;
        public bool Archived;
        public BlueprintVersionRecord[] Versions;
    }

    /// <summary>ERD-DAT-004 BuildingRecord。</summary>
    [Serializable]
    public sealed class BuildingRecord
    {
        public string BuildingId;
        public string BuildingTypeId;
        public string RegionId;
        public Vector2 Position;
        public float Rotation;
        public float Health;
        public BuildingConstructionState ConstructionState;
        public int PowerPriority;
        public BuildingPowerState PowerState;
        public CargoEntry[] Inventory;
        public string[] QueueIds;
        public string BlockedReason;
        /// <summary>ER3-SOFTLOCK-01 AC-ECO-012："非核心建筑拆除按实际投入50%返还"——建成/修复该建筑
        /// 时玩家实际花费的废料总额，在 <see cref="Regions.HomeValleyWorkOrders"/> 的 CompleteBuild/
        /// CompleteRepair 完成那一刻写入。开局即 Operational、从未走过建造/修复流程的建筑（装配站/
        /// 解析台/维修台/核心）恒为 0——玩家没有为它们实际投入过废料，拆除按字面"实际投入"返还 0，
        /// 是忠于卡片原文的设计取舍，不是遗漏。旧存档缺该字段时 JsonUtility 按 int 默认值 0 迁移，
        /// 效果等同于"从未真正花过钱"，安全。</summary>
        public int InvestedScrap;
    }

    /// <summary>ER3-STO-01：ERD-ECO-003 地面物——独立于仓库/核心缓存/机器货舱的第三类存放位置。
    /// 搬运"从来源预留、到达目标才提交"的语义里，尚未（或不再）被任何容器持有的物品必须有自己的
    /// 记录而不是凭空消失/凭空出现；机器搬运中阵亡、目标满、玩家取消/换目标，都会让物品落在地面。
    /// <see cref="SalvageInstanceId"/> 是"这一份具体掉落"的唯一标识（不是内容类型 ID）——同一来源
    /// （如同一处残骸节点）只产生一次，用于 100 次压力测试时判重/去重，防止同一个来源被重复领取
    /// 或在中断重试链路上复制出第二份。</summary>
    [Serializable]
    public sealed class GroundItemRecord
    {
        public string GroundItemId;
        public string RegionId;
        public Vector2 Position;
        public string ResourceType;
        public int Amount;
        public string SalvageInstanceId;
    }

    /// <summary>ERD-DAT-005 ResourceTransaction：跨帧资源消费的唯一凭证，取消/重试必须幂等。</summary>
    [Serializable]
    public sealed class ResourceTransactionRecord
    {
        public string TransactionId;
        public string OwnerId;
        public string ResourceType;
        public float Requested;
        public float Reserved;
        public float Consumed;
        public float Refundable;
        public ResourceTransactionState State;
        public string FailureReason;
    }

    /// <summary>ERD-DAT-006 EventLedger 的单条一次性事件记录。
    /// 事件键由 <c>campaignId</c>（CampaignState 自身持有）+ <see cref="EventId"/> 唯一；
    /// 读档重放通过 <see cref="CampaignSaveService"/> 之外的消费方按此键判重，不在本 Story 实现消费方。</summary>
    [Serializable]
    public sealed class EventLedgerEntry
    {
        public string EventId;
        /// <summary>如 ObjectiveComplete / FirstUnlock / RegionReward / BossPhaseReward /
        /// VictorySettlement / EmergencyRescue，纯字符串分类，不作为枚举以免锁死后续内容。</summary>
        public string Category;
        public float GrantedAtPlaySeconds;
        /// <summary>可选负载（如解锁的内容 ID、发放数量），JSON 字符串，允许为空。</summary>
        public string Payload;
    }

    /// <summary>ERD-WRK-001 WorkOrder。</summary>
    [Serializable]
    public sealed class WorkOrderRecord
    {
        public string WorkOrderId;
        public WorkOrderKind Kind;
        public string IssuerId;
        public string TargetId;
        public string SourceId;
        public string DestinationId;
        public string[] RequiredTags;
        public string ResourceTransactionId;
        public int Priority;
        public long CreatedTick;
        public int AssignedMachineLogicId;
        public WorkOrderState State;
        public string FailureReason;
        public int RetryCount;
        /// <summary>ER3-WRK-01：补齐 ER1-SAVE-01 遗留的骨架缺口（此前无字段能表达"还剩多少"，
        /// 进程重启只能"整段时长重新计时"，见 ER2-SCENE-01/ER3-STO-01 的 RearmInterrupted* 兜底注释）。
        /// 含义按 <see cref="State"/> 区分：<see cref="WorkOrderState.InProgress"/> 时是"到目标后计时中的
        /// 工作时长"（秒），<see cref="WorkOrderState.Waiting"/> 且 <see cref="FailureReason"/> 为
        /// "path-blocked" 时借用同一字段表达"已等待秒数"（两种含义互斥，同一时刻只处于一种状态，
        /// 不会混淆）；其余状态下恒为 0，不承载意义。随 <see cref="WorkOrderRecord"/> 一起落盘，
        /// 读档直接从上次进度续期，不再需要 RearmInterrupted* 那类"整段重新计时"兜底。</summary>
        public float Progress;
        /// <summary>Progress 达到该值即完工/该等待窗口到期。语义同样按 <see cref="State"/> 区分。</summary>
        public float Duration;
    }

    /// <summary>ERD-FAC-001 工厂队列项。</summary>
    [Serializable]
    public sealed class FactoryQueueItemRecord
    {
        public string QueueItemId;
        public FactoryQueueKind Kind;
        public string BlueprintId;
        public int BlueprintVersion;
        public int TargetMachineLogicId;
        public string TransactionId;
        public float Duration;
        public float Progress;
        public FactoryQueueState State;
        public string BlockedReason;
        /// <summary>ER4-FAC-01：补齐 ER1-SAVE-01 遗留的骨架缺口（同 <see cref="WorkOrderRecord.Progress"/>
        /// 补齐先例）——"多订单按稳定队列顺序执行"需要一个不受 <see cref="CampaignState.NormalizeForSave"/>
        /// 按 <see cref="QueueItemId"/> 字典序排序影响的真实创建顺序，落盘保证读档后 FIFO 顺序不变。</summary>
        public long CreatedTick;
    }

    /// <summary>ERD-EXP-001 RegionRecord。铸造外围和核心共用一条记录（同一 RegionId）。</summary>
    [Serializable]
    public sealed class RegionRecord
    {
        public string RegionId;
        public RegionState State;
        public string[] DiscoveredNodes;
        public string[] DestroyedNodeIds;
        public string[] LootedContainerIds;
        public string[] LostQuestSalvageIds;
        public float EnemyAlertLevel;
        public string AdaptationId;
        public int ExpeditionCount;
        public bool CoreGateUnlocked;
        public string CoreState;
    }

    /// <summary>DEMO-CONTENT-LOCK.md：逐目标持久化 ObjectiveRecord，字段与该文档"必须作为可存档
    /// ObjectiveRecord 实现"一段一致（objectiveId/state/startedAtPlaySeconds/completedAtPlaySeconds/eventId）。
    /// 不在 ERD-DAT-001 的 CampaignState 必须字段表里，但 STORY-EXECUTION-CARDS.md #ER1-SAVE-01
    /// 明确点名要求本 Story 一并定义；CampaignState.CompletedObjectiveIds 仍按 ERD-DAT-001 原样保留，
    /// 二者语义不同：前者是完整状态机记录，后者是只读派生的"已完成 ID 集合"快照。</summary>
    [Serializable]
    public sealed class ObjectiveRecord
    {
        public string ObjectiveId;
        public ObjectiveState State;
        public float StartedAtPlaySeconds;
        public float CompletedAtPlaySeconds;
        public string EventId;
    }

    /// <summary>CampaignState.controlHandoff 的骨架占位。字段形状对齐现有
    /// <see cref="GameLogic.Progression.ControlHandoffSaveData"/>（v1），但**独立定义、不复用该类型**——
    /// SYSTEMS-SPEC.md 明确把 ControlHandoffState/ControlPersistence 并入 CampaignState 的工作
    /// 排到 ER1-SAVE-02，本 Story 只占位字段，不改旧文件行为，也不做真正的合并/迁移。
    /// 同样绝不落盘 SimEntityId。</summary>
    [Serializable]
    public sealed class ControlHandoffRecord
    {
        public int ControlledLogicId;
        public float AnchorX;
        public float AnchorY;
        public bool HasAnchor;
    }

    /// <summary>ER1-SAVE-01：当前进程内"正在游玩的战役"持有者。主菜单新建/继续/读取成功后写入，
    /// <see cref="CampaignAutoSaveService"/> 的六类自动存档点读取本类找到要保存的槽位与数据。
    /// 纯内存态，不落盘；下一局/回主菜单需显式 <see cref="Clear"/>，避免残留上一局引用
    /// （ERD-PER-002 生命周期红线）。</summary>
    public static class CampaignSession
    {
        public static CampaignState Current { get; private set; }
        public static int ActiveSlotIndex { get; private set; } = -1;

        public static bool HasActiveCampaign => Current != null && ActiveSlotIndex >= 0;

        public static void Set(int slotIndex, CampaignState state)
        {
            ActiveSlotIndex = slotIndex;
            Current = state;
        }

        public static void Clear()
        {
            ActiveSlotIndex = -1;
            Current = null;
        }
    }
}
