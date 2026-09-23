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

        // ── ER6-REACT-02：铸造重炮/熔穿过载武器状态（"热量Stat管线未建立"此前一直是 DEBT-ER4CONTENT01-XX
        // 的字面原文，本 Story 首次接入真实数据，见 Regions.CannonCombat 类注释）─────────────
        public float WeaponHeat;
        /// <summary>热量≥<see cref="Regions.FracturedCityLayout.WeaponHeatOverheatThreshold"/> 时置真，
        /// 必须降到 &lt;<see cref="Regions.FracturedCityLayout.WeaponHeatRecoverThreshold"/> 才清除——
        /// 迟滞（hysteresis）而不是单一阈值来回抖动，"低于60方可恢复"字面要求。</summary>
        public bool IsWeaponOverheated;
        /// <summary>重炮"1秒瞄准线"进行中标记——非0表示已开始瞄准，达到这个时间戳才真正开火；
        /// 0＝当前未在瞄准。</summary>
        public float CannonAimReadyAtPlaySeconds;
        /// <summary>重炮"3秒冷却"——低于这个时间戳前新的开火请求被拒绝（含瞄准环节，不是开火后才计）。</summary>
        public float NextCannonActionAtPlaySeconds;
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

        /// <summary>ER4-PRIM-03：9 槽与 <see cref="PrimitiveChipRecord.PartId"/> 的对应关系，
        /// 行优先 0～8，与 <see cref="CircuitSlotContentIds"/> 平行——后者记录"这个槽装的是什么内容"，
        /// 本字段记录"具体是仓里哪一个物理实例"。0/8 固定槽永远为 null（源/汇不占用基元仓实例）；
        /// 1～7 空槽同样为 null。旧档（本字段落地前的记录）迁移后本字段维持全 null——旧档迁移只保证
        /// 默认线合法可编译，不凭空造出玩家从未真正拥有过的仓内实例。刻意不参与
        /// <see cref="BlueprintCircuitBoard"/> 的 <c>ComputeSignature</c>：签名描述"内容"而非"哪个实例"，
        /// 两个物理实例只要 CardDefId 相同就应产出同一签名，不能让实例更替误判蓝图内容变化。</summary>
        public string[] CircuitSlotPartIds;
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

    /// <summary>ER4-PRIM-05 STORY-EXECUTION-CARDS.md 第1条"低威胁残骸靶"——归还谷地范围内的静止
    /// 战斗验证目标。落盘（而非纯运行时对象）是为了支持存读档往返后仍可复测同一目标（§5.1 第2步
    /// "保存退出并重启读取同一战役……比较同一目标/输入的签名和事件"），见
    /// <see cref="Regions.HomeValleyCombatTargets"/>。</summary>
    [Serializable]
    public sealed class CombatTargetRecord
    {
        public string TargetId;
        public string RegionId;
        public Vector2 Position;
        public float Health;
        public float MaxHealth;
        /// <summary>命中后短暂冷却重生用；不是"死亡"——低威胁残骸靶是可反复验证的靶标，
        /// 不是一次性可摧毁物（见 <see cref="Regions.HomeValleyCombatTargets"/> 类注释）。</summary>
        public float RegenCooldownRemaining;
    }

    /// <summary>ER4-PRIM-03 STORY-EXECUTION-CARDS.md 第1条："仓/草稿槽/待领取三态恰一"。</summary>
    public enum PrimitiveChipState
    {
        /// <summary>在基元仓里，未装入任何蓝图草稿。</summary>
        Bag,
        /// <summary>已装入某个蓝图（<see cref="PrimitiveChipRecord.DraftBlueprintId"/>/
        /// <see cref="PrimitiveChipRecord.DraftSlot"/>）的 1～7 号自由槽。</summary>
        Draft,
        /// <summary>仓满时新生成的实例排队等待玩家腾格领取，不丢失、不阻断来源事件。</summary>
        Pending,
    }

    /// <summary>ERD-PRM-003 战役唯一基元芯片实例账，`Campaign.Primitive.PrimitiveInventory` 的唯一
    /// 写入口写入。全 public 字段（非属性）以兼容 <see cref="JsonUtility"/>——与既有
    /// <see cref="GameLogic.MetabolicSlice.Bag.PartInstance"/>（只读属性，不可直接 JsonUtility 序列化）
    /// 不是同一个类型：本类型是该实例在"战役存档层"的落盘投影，<c>PartId</c> 与
    /// <see cref="GameLogic.MetabolicSlice.Bag.PartInstance.PartId"/> 同一命名空间但生成规则独立
    /// （"pchip_"前缀 GUID，与 <see cref="GroundItemRecord.SalvageInstanceId"/>/
    /// <see cref="CampaignState.UnlockedContentIds"/>/<see cref="MachineRecord.LogicId"/> 三套 id
    /// 空间互不相交——STORY-EXECUTION-CARDS.md 第1条"实例ID不可与远征货物、解锁目录、机器成品混用"的
    /// 具体落点）。</summary>
    [Serializable]
    public sealed class PrimitiveChipRecord
    {
        public string PartId;
        /// <summary><see cref="GameLogic.MetabolicSlice.CardDefs.CardCatalog"/> 的条目 id
        /// （Demo 范围内恒为 <c>"organ_focus"</c>="聚焦镜"，<c>"organ_focus_plus"</c> 精校镜是
        /// ER4-PRIM-04 合成产物，字段结构上已支持，未来无需改 schema）。</summary>
        public string CardDefId;
        public PrimitiveChipState State;
        /// <summary>仅 <see cref="PrimitiveChipState.Draft"/> 有效：当前装在哪个 BlueprintId 的电路草稿里。</summary>
        public string DraftBlueprintId;
        /// <summary>仅 <see cref="PrimitiveChipState.Draft"/> 有效：装在该蓝图的第几号槽（1～7）。</summary>
        public int DraftSlot = -1;
        /// <summary>解析来源的 salvageInstanceId，去重用（同一来源只生成一次实例）；补印来源为 null。</summary>
        public string SourceSalvageId;

        /// <summary>ER4-PRIM-04：非空时表示该实例已被某个 <see cref="CraftQueueItemRecord"/>（值即其
        /// <c>TransactionId</c>）预留为合成/拆解材料——"原子登记 reservedByTransactionId……仓占用暂不
        /// 下降，UI 显示已预留/不可装拆"（PRIMITIVE-FULL-DEMO-SPEC.md §4.2）。预留期间仍是 Bag 态
        /// （占仓格），但 <c>PrimitiveInventory.TryMoveToDraft</c>/<c>TryMoveToBag</c>/
        /// <c>TryClaimPending</c> 必须拒绝对被预留实例的操作，防止同一实例被合成台与电路板面板同时
        /// 拿走。</summary>
        public string ReservedByTransactionId;
    }

    /// <summary>ER4-PRIM-04 STORY-EXECUTION-CARDS.md：合成台唯一队列项。<c>PrimitiveCraftStation</c>
    /// 唯一写入口，不得在别处直接改 <see cref="CampaignState.CraftQueues"/>——与
    /// <see cref="HomeValleyFactory"/>/<see cref="FactoryQueueItemRecord"/> 同一结构模式（单 Running
    /// 工位 FIFO、<c>CreatedTick</c> 稳定排序）。</summary>
    [Serializable]
    public sealed class CraftQueueItemRecord
    {
        public string QueueItemId;
        public CraftQueueKind Kind;
        /// <summary>固定长度2：Upgrade 用两个槽（MaterialPartIds[1] 非空），Disassemble 只用第一个
        /// （MaterialPartIds[1] 恒为 null/空串）。</summary>
        public string[] MaterialPartIds = { null, null };
        /// <summary>废料侧资源事务 id（Upgrade=消费5废料，Disassemble=产出2废料）；材料实例本身的锁
        /// 由 <see cref="PrimitiveChipRecord.ReservedByTransactionId"/>（值即本字段/本项 QueueItemId）
        /// 承载，不经 <see cref="CampaignEconomyLedger"/>（后者只管三顶层资源，不管物品实例）。</summary>
        public string TransactionId;
        public float Duration;
        public float Progress;
        public CraftQueueState State;
        public string BlockedReason;
        /// <summary>Upgrade 完成后新生成的 focus_plus 实例 PartId（Bag 或 Pending 态）；Disassemble 恒
        /// 为 null（产出是废料不是实例）。</summary>
        public string OutputPartId;
        public long CreatedTick;
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

    /// <summary>ER6-EXPOSE-01：信号暴露 HUD 明细唯一权威来源——每笔批准来源的暴露变化独立一条记录，
    /// "净值不合并成神秘数值"（摧毁监听节点同时产生+8/-15两条独立记录）。与 <see cref="EventLedgerEntry"/>
    /// 是同一批次写入的两份不同用途记录（后者管幂等判重，本类型管玩家可读的"发生了什么/改了多少"），
    /// 唯一写入口 <see cref="CampaignExposureLedger"/>。</summary>
    [Serializable]
    public sealed class SignalExposureEventRecord
    {
        public string EventId;
        /// <summary>玩家可读来源文案，如"重型机生产"/"摧毁节点"/"塔关广播"。</summary>
        public string Source;
        public float Delta;
        public float AtPlaySeconds;
        /// <summary>本笔结算后的暴露值（钳制后），供 HUD 直接展示"当时到了多少"而不必重放全部历史。</summary>
        public float ResultingExposure;
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
        /// <summary>ER7-CORE-01：<see cref="CoreBossState"/> 的字符串存储（<see cref="Enum.ToString()"/>），
        /// 唯一写入口 <see cref="Regions.FoundryOutpostCoreBoss.TryTransition"/>。null/空＝
        /// <see cref="CoreBossState.Locked"/>（尚未首次进入核心分区）。</summary>
        public string CoreState;

        // ── ER7-CORE-01：Boss 战 FSM/计时/一次性标记——只在 FoundryOutpost 的 RegionRecord 上有意义
        // （同 AdaptationId/CoreGateUnlocked 先例，共享类但只对特定区域字段有效）。两供能节点+主核心
        // 的 HP/存活本身**不**在这里重复记账——它们就是三条 <see cref="RegionEnemyRecord"/>
        // （EnemyTypeId="boss_node"/"boss_core"），复用该类型已有的 Health/MaxHealth/IsAlive/
        // CycleCooldownRemaining 字段与既有 <see cref="Regions.FoundryOutpostRegion.TryDamageEnemy"/>/
        // <see cref="Regions.CannonCombat.TryFire"/> 伤害结算路径（含铸造重炮/熔穿过载），不新造第二套
        // 伤害管线。唯一写入口全部集中在 Regions.FoundryOutpostCoreBoss，不在别处散落改这些字段。──
        /// <summary>Transition 阶段结束的 PlaySeconds 时间戳，0＝当前不在 Transition。</summary>
        public float CoreTransitionEndAtPlaySeconds;
        /// <summary>"只召一台维修机"——一次性标记，Transition 每次触发只允许召唤一次。</summary>
        public bool CoreTransitionRepairBotSummoned;
        /// <summary>核心数据盒是否已经掉落过——"主核心毁灭只掉一次核心数据盒"幂等标记。</summary>
        public bool CoreDataDropped;
        /// <summary>Phase2 区域封锁预警的 PlaySeconds 时间戳，0＝未触发。达到该时间戳后
        /// <see cref="CoreLockoutActive"/> 才真正置真（"提前2秒可见/可听提示"）。</summary>
        public float CoreLockoutWarnAtPlaySeconds;
        public bool CoreLockoutActive;

        /// <summary>ER6-EXPOSE-01："一次远征中每累计30秒直控+5"——按本区域当次远征累计直控秒数，
        /// 在 <see cref="Regions.ExpeditionDepartureService.TryDepart"/> 每次真正出发时清零（"一次
        /// 远征"的边界），唯一写入口 <see cref="CampaignExposureLedger.TickDirectControlExposure"/>。</summary>
        public float DirectControlAccumulatedSeconds;
        /// <summary>ER5-SILENT-01：静默侦察机在玩家机器身上留下的检测标记，唯一写入口
        /// <see cref="Regions.FracturedCityRegion.TryMarkMachine"/>/<see cref="Regions.FracturedCityRegion.TryClearMark"/>。
        /// 与 ER6-REACT-01 未来"标记跳转"反应系统（玩家武器标记敌方目标）是两个独立概念——那一套
        /// 标记加在 <see cref="RegionEnemyRecord"/> 上，这一套加在玩家 <see cref="MachineRecord"/>
        /// 上，互不共享存储也互不干扰判定。</summary>
        public MarkedMachineRecord[] MarkedMachines;

        /// <summary>ER6-REACT-01：标记跳转反应的"敌方标记"——玩家装配静默标记器命中敌人时打上，供
        /// 标记跳转固件识别跳转目标。与 <see cref="MarkedMachines"/>（静默侦察机标记玩家机器，ER5-SILENT-01）
        /// 是同一"标记"词汇下两个完全独立的方向（谁标记谁、用途都不同），互不共享存储。唯一写入口
        /// <see cref="Regions.FracturedCityRegion.TryMarkEnemy"/>/<see cref="Regions.FracturedCityRegion.TryClearEnemyMark"/>。</summary>
        public MarkedEnemyRecord[] MarkedEnemies;
    }

    /// <summary>见 <see cref="RegionRecord.MarkedMachines"/> 类注释。</summary>
    [Serializable]
    public sealed class MarkedMachineRecord
    {
        public int MachineLogicId;
        public float ExpireAtPlaySeconds;
    }

    /// <summary>见 <see cref="RegionRecord.MarkedEnemies"/> 类注释。</summary>
    [Serializable]
    public sealed class MarkedEnemyRecord
    {
        public string EnemyInstanceId;
        public float ExpireAtPlaySeconds;
    }

    /// <summary>ER5-REGION-01：远征区域内的敌方/节点实例（静默侦察机、静默干扰机等）。与家园
    /// <see cref="MachineRecord"/>（玩家机队，有 LogicId/蓝图/货舱等玩家侧语义）是两套独立记录——
    /// 敌方只需要"稳定实例 ID + 位置 + 血量 + 存活"这一最小集合，不接 MachineLoadoutRegistry/
    /// BlueprintCircuitCompiler（那是玩家装配链路）。真正的敌方 AI 行为树/阵型属于 ER5-SILENT-01，
    /// 本类型只是它将要写入的数据骨架（同 ER1-SAVE-01 对 RegionRecord 的"骨架先行"先例）。</summary>
    [Serializable]
    public sealed class RegionEnemyRecord
    {
        public string EnemyInstanceId;
        public string RegionId;
        /// <summary><see cref="Content.EnemyCatalog"/> 的条目 id（如 enemy_scout/enemy_jammer）。</summary>
        public string EnemyTypeId;
        public Vector2 Position;
        public float Health;
        public float MaxHealth;
        public bool IsAlive;
        /// <summary>周期性节奏计时器，语义按 <see cref="EnemyTypeId"/> 区分（静默侦察机＝标记冷却，
        /// DEMO-CONTENT-LOCK.md §4.1"每8秒标记"）。</summary>
        public float CycleCooldownRemaining;
        /// <summary>ER6-FOUNDRY-01：第二个通用计时槽，语义同样按 <see cref="EnemyTypeId"/> 区分（同
        /// <see cref="CycleCooldownRemaining"/> 先例，不为每个敌类型各开一个专属字段）——铸造步进炮＝
        /// 瞄准剩余秒数（&gt;0 表示已进入"1秒瞄准线"预警阶段，归零时才真正开火，DEMO-CONTENT-LOCK.md
        /// §5"1秒瞄准线"）；其余敌类型恒为 0，不承载意义。</summary>
        public float SecondaryTimer;
    }

    /// <summary>ER5-REGION-01 STORY-EXECUTION-CARDS.md 第2条"货物 Lost/Recovered 写 RegionRecord"
    /// + DEMO-CONTENT-LOCK.md §4.1 第4条"关键模块若已从地面拾取但撤离失败，原掉落实例标记 Lost，
    /// 下一次进入……由同一任务恢复柜重生成一份同 contentId、不同 salvageInstanceId 的保底件"。
    /// 关键任务物（标记器模块/协议数据盒）需要 OnGround→Carried→Recovered/Lost 这条独立生命周期，
    /// 不能复用 <see cref="GroundItemRecord"/>（无"已被某台机器携带"状态）或
    /// <see cref="CargoEntry"/>（无实例身份，无法区分"丢失的具体是哪一份"）。</summary>
    [Serializable]
    public enum RegionQuestItemState
    {
        OnGround = 0,
        Carried = 1,
        Recovered = 2,
        Lost = 3,
    }

    [Serializable]
    public sealed class RegionQuestItemRecord
    {
        public string SalvageInstanceId;
        public string RegionId;
        /// <summary>如 <see cref="Regions.FracturedCityLayout.MarkerModuleContentId"/>/
        /// <see cref="Regions.FracturedCityLayout.ProtocolDataboxContentId"/>。</summary>
        public string ContentId;
        public RegionQuestItemState State;
        /// <summary>仅 <see cref="RegionQuestItemState.Carried"/> 有效：当前由哪台机器携带
        /// （<see cref="MachineRecord.LogicId"/>）。</summary>
        public int CarrierLogicId;
        /// <summary>仅 <see cref="RegionQuestItemState.OnGround"/> 有效。</summary>
        public Vector2 Position;
    }

    /// <summary>ER6-ANA-01：解析台队列状态——同 <see cref="CraftQueueState"/> 同一命名/语义纪律
    /// （断电转 WaitingPower 且保留进度，不是本 Story 另起一套新词汇）。</summary>
    [Serializable]
    public enum AnalysisQueueState
    {
        Queued = 0,
        Running = 1,
        WaitingPower = 2,
        Completed = 3,
        Cancelled = 4,
        Failed = 5,
    }

    /// <summary>ER6-ANA-01 STORY-EXECUTION-CARDS.md："未归档模块在货舱/地面/仓库/搬运中/解析台五态
    /// 只有一个真持有者"——货舱/地面两态已经是 <see cref="RegionQuestItemRecord"/>
    /// 的 Carried/OnGround（区域内，未改动）；一旦 Recovered，同一 <see cref="RegionQuestItemRecord.SalvageInstanceId"/>
    /// 只可能处于本类型定义的家园侧状态之一：Recovered 且没有任何 <see cref="AnalysisQueueItemRecord"/>
    /// 引用它＝"仓库"（未排队）；本记录 <see cref="State"/>==Queued＝"搬运中"（送去解析台排队中，本
    /// Demo 不做真实运输在途时间，同 <see cref="HomeValleyCargo"/> 先例）；==Running＝"解析台"（正在
    /// 解析，进度真实推进）；==Completed/Cancelled/Failed 是终态。<see cref="RegionQuestItemRecord.State"/>
    /// 一旦变 Recovered 就不会再变回 Carried/OnGround（<see cref="Regions.FracturedCityRegion.ResolveExtraction"/>
    /// 只处理当前 Carried 态条目），"不让同一 salvageInstanceId 同时装车和解析"因此是结构性保证，不需要
    /// 额外互斥校验。</summary>
    [Serializable]
    public sealed class AnalysisQueueItemRecord
    {
        public string QueueItemId;
        public string SalvageInstanceId;
        /// <summary>如 <see cref="Regions.FracturedCityLayout.MarkerModuleContentId"/>——用于查
        /// <see cref="Regions.HomeValleyAnalysis.YieldTable"/> 取解锁目标/技术数据/时长。</summary>
        public string ContentId;
        public float Duration;
        public float Progress;
        public AnalysisQueueState State;
        public string BlockedReason;
        public long CreatedTick;
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
