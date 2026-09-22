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

    /// <summary>ERD-WRK-002 五类工作优先级（Haul/Build/Repair/Salvage/Recharge），定长 5，
    /// 下标对应 <see cref="WorkOrderKind"/> 的枚举值。数值越大优先级越高，本 Story 只落盘骨架，
    /// 不实现分配算法（ER3-WRK-02）。</summary>
    [Serializable]
    public sealed class WorkPriorities
    {
        public int Haul;
        public int Build;
        public int Repair;
        public int Salvage;
        public int Recharge;
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
