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
                case WorkOrderKind.Deliver: return Haul; // FG4-ECO-06：补给是搬运的一种，按机器的“搬运”优先级领单。
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

    /// <summary>FG4-ECO-07（FG04 FGR-ECO-040）：机器岗位。整数值进存档（JsonUtility 存枚举的整数），与 fg.TbRosterRole.code 一一对应（自检逐项比对），
    /// 顺序不能改、只能在末尾追加。0 = 劳动：旧档缺字段读成劳动，与 0.2 之前“所有机器都接活”的行为一致。</summary>
    public enum MachineRole
    {
        Labor = 0,
        Garrison = 1,
        OutpostLabor = 2,
        ExpeditionReserve = 3,
        Idle = 4,
        /// <summary>只由在办的送修工单决定（名册里选它 = 送修）；不进存档字段。</summary>
        InRepair = 5,
        /// <summary>只由所在地点决定（机器不在家园且活着）；不能在名册里选。</summary>
        OnExpedition = 6,
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
        /// <summary>FG1-HUD-01（FGR-SIG-082 机器经历“与信号同行”次数）：信号进入这台机器的次数（接入完成、Tab 切换、跳转、阵亡回弹到它）。
        /// 读档恢复“信号仍在这台机器里”不算新的一次。唯一写入口 <c>SignalUplinkService.SetUplink</c>。旧档缺字段读回 0。</summary>
        public int SignalUplinkCount;
        /// <summary>FG1-HUD-01（FGR-SIG-082 累计时长）：信号已离开的各段在这台机器里累计的统一时钟步数（<c>GameClock.Ticks</c>，60 步 = 1 游戏秒；
        /// 存整数步，不存浮点秒——浮点经 JsonUtility 往返会差 1 ulp）。当前正在进行的一段不在这里，读取走 <c>MachineSignalExperience.TotalTicks</c>。</summary>
        public long SignalUplinkTicks;

        // ── FG4-ECO-07（FG04 FGR-ECO-040 / 041）：岗位与名字。只加字段、不升存档版本：旧档缺字段读成 0 = 劳动、空名字 = 默认名。

        /// <summary>玩家设定的岗位（只存可设定的五种：劳动 / 驻防 / 前哨劳动 / 远征预备 / 闲置；“远征中”“维修中”是按所在地点与在办工单算出来的，
        /// 见 <see cref="MachineRoster.EffectiveRole"/>）。唯一写入口 <see cref="MachineRoster.TrySetRole"/>。</summary>
        public MachineRole Role;

        /// <summary>玩家起的名字（空 = 默认名“型号 #编号”）。唯一写入口 <see cref="MachineNaming.TryRename"/>；显示一律走 <see cref="MachineNaming"/>。</summary>
        public string CustomName;

        /// <summary>“驻防”岗位的驻防点（建筑 ID；空 = 归还核心）。</summary>
        public string RolePointId;

        /// <summary>名册为这台机器开的最近一张驻防 / 送修工单（岗位维持器据此判断“玩家接管过就不再拉回去”）。</summary>
        public string RoleOrderId;

        /// <summary>
        /// FG6-DEF-06（FGR-DEF-043“按玩家画的巡逻路线巡逻”，承接 DEBT-FG4ECO07-02）：驻防岗的巡逻点（建筑 ID，按顺序；空 / null = 守在驻防点不巡逻）。
        /// 路线 = 驻防点 → 巡逻点 1 → … → 回到驻防点，循环。唯一写入口 <see cref="MachineRoster.TryAddPatrolPoint"/> / <see cref="MachineRoster.TryClearPatrol"/>。只加字段、不升存档版本。
        /// </summary>
        public string[] PatrolPoints;

        /// <summary>巡逻路线上正在去的那个点（0 = 驻防点，1… = 巡逻点）。按步序号推进（与观察无关），存档。</summary>
        public int PatrolIndex;

        /// <summary>
        /// FG6-DEF-06 审查修复（P1）：到不了而暂时跳过的巡逻点（建筑 ID）与跳过时的步序号（<see cref="PatrolSkipTicks"/>，一一对应）。
        /// 跳过期间路线不去这一点（不再反复开单 / 失败），过 roster.patrol_retry_seconds 再试；“到不了”的通知每个点只发一次，直到机器走到过它（那时移出本表）。
        /// 改巡逻路线 / 清空 / 重设驻防时清空。存档；旧档缺字段 = 没有跳过的点。
        /// </summary>
        public string[] PatrolSkipIds;

        /// <summary>与 <see cref="PatrolSkipIds"/> 一一对应：最近一次判定到不了的步序号（GameClock.Ticks）。</summary>
        public long[] PatrolSkipTicks;

        // ── FG5-RND-06（FG05 FGR-RND-060 纪念墙“阵亡地点”；FG13 FGU-40 按时间 / 地点排序）：阵亡那一刻记下的时刻与地点。
        // 唯一写入口 MachineRegistry.MarkDeadByLogicId（存活 → 阵亡的唯一翻转点）。只加字段、不升存档版本：旧档缺字段读成 0 / 空 = “时间 / 地点未记录”。

        /// <summary>阵亡的世界步（<c>GameClock.Ticks</c>；0 = 没有记录，例如 FG5-RND-06 之前的旧档）。</summary>
        public long DeathTick;

        /// <summary>阵亡时所在的地点（区域 ID；与 <see cref="RegionId"/> 分开记，之后的结算改不到它）。空 = 没有记录。</summary>
        public string DeathRegionId;

        /// <summary>阵亡位置（该地点的平面坐标，米）。</summary>
        public Vector2 DeathPosition;

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

        /// <summary>FG1-SIG-02（FGR-SIG-020）：接入口所在的格（1～7）。0 = 没有接入口——0 号是源点，永远不能标，
        /// 所以旧档缺这个字段时读回的 0 正好就是“没有接入口”，不用升存档版本。参与签名（有接入口时）。</summary>
        public int CircuitUplinkSlot;
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
        /// <summary>占地的几何中心（世界 XZ）。FG0-ARCH-04 起由格网唯一计算（<see cref="Grid.HomeGridService"/>），
        /// 机器走过去施工 / 维修、可视化、通知定位都读它；不要单独改它。</summary>
        public Vector2 Position;
        /// <summary>FG0-ARCH-04（FGR-ARC-001 / FG-GAP-006）：朝向，0 / 90 / 180 / 270（俯视顺时针），决定占地与端口方向。
        /// 唯一写入口是 <see cref="Grid.HomeGridService"/>（开局布局、放置、旋转、旧档迁移）。</summary>
        public float Rotation;
        /// <summary>FG0-ARCH-04：枢轴格（格网坐标，1 格 = 1 米）。占地 = 枢轴格 + fg.TbBuildingGrid 的尺寸按 <see cref="Rotation"/> 旋转。
        /// 旧存档没有这两个字段时由 <see cref="Grid.HomeGridService.EnsureMigrated"/> 按 <see cref="Position"/> 补齐。</summary>
        public int GridX;
        public int GridY;
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
        /// <summary>FG3-LOG-01（FGR-LOG-008 搬迁）：不为空时，这条记录是“搬迁目标”虚影——机器在这里施工完成后，
        /// 原建筑（本字段指向它的 BuildingId）从原位置移除，本记录接过原建筑的 ID、生命、库存、队列、电力优先级与投入，
        /// 变成原建筑本身（设置保留）。施工完成前原建筑照常运转；取消虚影 = 取消搬迁，原建筑不受影响。
        /// 旧存档没有这个字段时 JsonUtility 补 null（= 普通建筑），无需迁移。唯一写入口是 <see cref="Grid.HomeGridService.TryRelocate"/>。</summary>
        public string RelocateFromId;
        /// <summary>FG3-LOG-02（FGR-LOG-006 虚影施工）：这座虚影施工一共要多少材料（废料）。放置时按建造表写入；搬迁虚影为 0（材料就是原建筑本身）。
        /// 建成后清零（投入记在 <see cref="InvestedScrap"/>）。旧存档没有这个字段时为 0，读档时由 <see cref="Regions.HomeValleyConstruction.MigrateLegacyBuildOrder"/> 按旧的“放置即预留”补齐。</summary>
        public int ConstructionRequired;
        /// <summary>FG3-LOG-02：机器已经从仓库运到现场、放进这座虚影的材料（累计）。施工进度不能超过“已到材料 / 所需材料”；
        /// 取消虚影时这部分全额退回（仓库满了就变成地面物，机器之后搬走）；施工中被摧毁时已消耗部分按比例掉落为地面物。</summary>
        public int ConstructionDelivered;
        /// <summary>FG4-ECO-11（承接 DEBT-FG3LOG02-02 多材料造价）：废料之外这座虚影要的材料——资源类型（= 物品 ID，fg.TbBuildMaterial），与
        /// <see cref="ExtraRequired"/> / <see cref="ExtraDelivered"/> 下标平行。放置 / 升级时按表写入，机器与废料同一套取料腿逐种取来；进度按每种材料
        /// “已到 / 所需”里最小的那个封顶；取消全额退回，被摧毁时已消耗部分按比例掉落。建成后清空（投入记在 <see cref="InvestedExtraIds"/>）。
        /// 旧存档没有这些字段 = 空（只要废料）。唯一写入口 <see cref="Regions.HomeValleyConstruction"/> / <see cref="Regions.HomeValleyWorkOrders"/>。</summary>
        public string[] ExtraMaterialIds;
        public int[] ExtraRequired;
        public int[] ExtraDelivered;
        /// <summary>FG4-ECO-11：建成 / 升级进这座建筑的非废料材料（例如超控阵列的关键材料），与 <see cref="InvestedExtraAmounts"/> 平行。
        /// 拆除时全额退回（关键材料回核心保管库）；被摧毁时留在建筑里（关键材料永不消失，FG08），重建只收废料。旧存档没有 = 空。</summary>
        public string[] InvestedExtraIds;
        public int[] InvestedExtraAmounts;
        /// <summary>FG4-ECO-05（FGR-ECO-011 改名）：玩家起的名字（null / 空 = 默认名“类型名”）。面板、悬停、通知、施工队列都显示它。
        /// 唯一写入口 <see cref="Economy.BuildingOps.TryRename"/>；搬迁 / 升级完工时跟着建筑走。旧存档没有这个字段 = 默认名。</summary>
        public string CustomName;
        /// <summary>FG4-ECO-05（FGR-ECO-012 原地升级）：等级（fg.TbBuildingTier；0 / 1 = T1）。只对表里有等级的建筑有意义（仓库、信号塔）。
        /// 升级目标虚影上写“升到第几级”，完工时写回原建筑。唯一写入口 <see cref="Grid.HomeGridService.TryUpgrade"/> / 完工换位。</summary>
        public int Tier;
        /// <summary>FG4-ECO-05（FG04 第 4 节“仓库支持只存某些物品”）：仓库只存哪些物品——空 = 全部可存物品；“cat:类别” = 只存这一类；“item:物品ID” = 只存这一种。
        /// 只影响这座仓库给哪些物品提供容量（已经存着的不丢）。唯一写入口 <see cref="Economy.BuildingOps.TrySetStoreFilter"/>。</summary>
        public string StoreFilter;
        /// <summary>FG4-ECO-05（FGR-BASE-020“设置保留”）：被摧毁那一刻是不是玩家禁用的状态——重建完工时恢复成禁用，不替玩家重新开机。
        /// 唯一写入口 <see cref="Regions.HomeValleyPowerGrid.ApplyBuildingDestroyed"/>，重建完工时读取并清掉。旧存档没有这个字段 = false（= 重建后运转）。</summary>
        public bool DisabledWhenDestroyed;
        /// <summary>FG6-DEF-03（FGR-DEF-014“突袭时优先修理正在受攻击的目标”）：最近一次耐久下降的统一时钟步（0 = 没挨过打）。
        /// 写入口：<see cref="Economy.BuildingOps.ApplyDamage"/>（普通建筑）、防御 / 炮塔对账发现内核耐久下降时（结构单位 / 炮塔单位）。
        /// 只读者：维修无人机找目标。存档里保留（读档接着跑与不存档一致）；旧存档没有 = 0。</summary>
        public long LastHitTick;
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
        /// <summary>FG1-SIG-01：装在信号核的某个槽位里（槽位真相是 <see cref="SignalCoreState.SlotPartIds"/>，
        /// 唯一写入口 <c>Signal.SignalCoreService</c>）。不占基元仓格子；只追加在末尾（存档里按整数保存）。</summary>
        SignalCore,
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

        /// <summary>FG2-FW-05（FGR-FW-061）：玩家锁定了这枚芯片——批量分解一律跳过它（装进信号核不受影响）。
        /// 唯一写入口 <c>PrimitiveInventory.TrySetLocked</c>。旧存档没有这个字段 = 未锁定。</summary>
        public bool Locked;

        /// <summary>FG2-FW-05（FG02 第 6 章“固件实例：来源”）：这枚芯片从哪来——seed 开局 / print 刻印 / salvage 解析 /
        /// encrypted 带回的加密固件 / craft 合成台。创建时由 <c>PrimitiveInventory</c> 写入；旧存档为空（显示“来源未记录”）。</summary>
        public string Origin;

        /// <summary>FG2-FW-05：获得时刻（战役时间毫秒，与工厂队列同一换算），固件库“最新获得”排序用。旧存档为 0。</summary>
        public long AcquiredTick;
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

    /// <summary>FG2-FW-04（FGR-FW-043）：一条具名反应在本存档里第一次报出名字的触发。</summary>
    [Serializable]
    public sealed class ReactionFirstTriggerRecord
    {
        public string ReactionId = string.Empty;
        /// <summary>统一时钟第几步（整数步，存读档逐字段一致）。</summary>
        public long Tick;
        /// <summary>发生在哪个地点（图鉴“首次触发于……”）。</summary>
        public string SiteId = string.Empty;
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
        /// <summary>FG1-SIG-06：来源种类（<see cref="ExposureSourceKind"/> 的常量）——暴露面板按它 + <see cref="Detail"/> 用当前语言显示来源名。
        /// 旧档（规则改写前）为空，读档时由 <see cref="CampaignExposureLedger.MigrateLegacy"/> 按 <see cref="EventId"/> 前缀补上。</summary>
        public string Kind;
        /// <summary>FG1-SIG-06：来源细节（固件 ID、用电量等），显示时代入来源名。</summary>
        public string Detail;
        /// <summary>FG1-SIG-06：来源阵营键（reclaim / silent / foundry / none），暴露面板“各阵营贡献”按它汇总。</summary>
        public string Faction;
        /// <summary>FG1-SIG-06：登记序号（单调递增）——“最近的来源”按它倒序（同一游戏时刻的几笔也有确定顺序）。</summary>
        public int Seq;
    }

    /// <summary>FG1-SIG-06（FGU-44“各阵营贡献”）：按（来源种类，阵营）累计的暴露增减——明细只保留最近若干条，汇总不随明细截断丢失。</summary>
    [Serializable]
    public sealed class SignalExposureTotalRecord
    {
        public string Kind;
        public string Faction;
        /// <summary>累计增加（正数部分）。</summary>
        public float Added;
        /// <summary>累计降低（负数部分的绝对值）。</summary>
        public float Removed;
        public int Count;
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
        /// <summary>FG0-ARCH-06：这张工单已经发过一次“无法到达”通知（之后 30 秒一次的重试不再刷屏；随存档保留，读档后也不重复发）。</summary>
        public bool UnreachableNotified;
        /// <summary>FG3-LOG-02（FGR-LOG-006 机器取料施工）：施工单当前这一腿。0 = 去现场施工（旧存档与搬迁虚影恒为 0）；
        /// 1 = 先去仓库取材料（到了仓库装货后转为 0，再去现场）。随存档保留，读档后机器按这一腿续走。</summary>
        public int Leg;
        /// <summary>FG3-LOG-02：这张施工单已经从仓库取过几趟材料（取料的资源事务 ID 按它编号，读档重放也不重号）。</summary>
        public int FetchCount;
        /// <summary>FG4-ECO-05（FGR-ECO-013 维修消耗维修件）：维修单开单时从家园库存预留的物品（维修件）与数量。完工即消耗；
        /// 取消 / 目标被摧毁 / 目标被拆时全额退回仓库（放不下的放在建筑旁边）。空 = 这张单没有预留物品（重建走废料事务）。</summary>
        public string ReservedItemId;
        public int ReservedItemAmount;
        /// <summary>FG4-ECO-06（FGR-ECO-031 可追溯）：派出 / 改动这张工单的常驻规则编号（3 = “由规则 R3 触发”；0 = 不是规则派的）。</summary>
        public int RuleSerial;
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

        // ── FG4-ECO-03：装配站从产线取料（FG04“机器 | 装配站 | 按蓝图（底盘、组件、模块、电子件）”）。旧存档的队列项 MaterialMode = false，照旧按废料事务结算。──
        /// <summary>按材料结算（入队时锁定材料清单；开工时从装配站材料缓存 → 仓库取，缺的按设置用废料代付）。</summary>
        public bool MaterialMode;
        /// <summary>入队时锁定的材料清单（生产 = 蓝图全部材料；改造 = 新蓝图比旧蓝图多出来的材料）。</summary>
        public ItemStackRecord[] Materials;
        /// <summary>全部用废料代付时的废料价（生产 = 蓝图版本的废料价；改造 = 新旧废料价差额）。缺一部分材料时按废料当量比例折算。</summary>
        public int ScrapPrice;
        /// <summary>开工时实际取走的材料（取消 / 装配站被毁 / 出厂失败时退回仓库；完工时消耗）。</summary>
        public ItemStackRecord[] Taken;
        /// <summary>已经取过料（开工）。不能用 <see cref="Taken"/> 是否为 null 判断：存档往返后空数组与 null 不分。</summary>
        public bool MaterialsTaken;
        /// <summary>开工时用废料代付的数量（消费型事务 <see cref="SubstituteTxId"/>，退款同上）。</summary>
        public int SubstituteScrap;
        public string SubstituteTxId;
        /// <summary>等待材料时各物品还差多少（面板 / 诊断显示；开工后清空）。</summary>
        public ItemStackRecord[] Shortfall;
        /// <summary>等待材料且废料代付也不够时：需要 / 现有的废料。</summary>
        public int ScrapShortNeed;
        public int ScrapShortHave;
        /// <summary>路线被堵时那一句原因（与固件库详情页同一个判定与文字；开工后清空）。</summary>
        public string RouteText;
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

        /// <summary>ER6-EXPOSE-01 旧字段（Demo“一次远征中每累计30秒直控+5”）。FG1-SIG-06 起接入时长不再计入暴露：
        /// 不再累计，出发与旧档迁移（<see cref="CampaignExposureLedger.MigrateLegacy"/>）时清零；只为旧档兼容保留。</summary>
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

        // ── FG5-RND-02（解析台 2.0）：三类敌方物品 ──────────────────────────────────────────
        /// <summary>来源：空 = Demo 区域任务物（<see cref="SalvageInstanceId"/> 指向 <see cref="RegionQuestItemRecord"/>，按 YieldTable 结算）；
        /// <see cref="Regions.HomeValleyAnalysis.SourceItem"/> = 家园里的一件敌方物品（仓库 / 传送带送进来，入队时已从库存取走）；
        /// <see cref="Regions.HomeValleyAnalysis.SourceChip"/> = 固件库里那枚未破解的固件芯片（引用，不取走、不消耗）。</summary>
        public string Source;
        /// <summary>物品种类（fg.TbEcoItem.id：unparsed_module / encrypted_firmware / data_core）。Demo 区域任务物为空。</summary>
        public string ItemId;
        /// <summary>物品身份：未解析模块 = 解锁的组件 / 模块 ID；加密固件 = 固件 ID；数据核心 = 资料 ID（fg.TbAnalysisLore）。空 = 身份不明（按重复解析）。</summary>
        public string TargetId;
        /// <summary>入队时取用的身份标签（取消时原样放回身份清单）。</summary>
        public string TagId;
        public string TagOrigin;
        public long TagAcquiredTick;
        /// <summary>来源 = 固件芯片时，被引用的芯片实例（<see cref="PrimitiveChipRecord.PartId"/>）。</summary>
        public string ChipPartId;
        /// <summary>完成时写入：这次是不是首次解析、给了多少技术数据（面板“最近结束”一栏与离家报告用）。</summary>
        public bool FirstTime;
        public int TechGained;
    }

    /// <summary>
    /// FG5-RND-02：家园里一件敌方物品的“身份”。物品本身在仓库 / 传送带 / 地面上是可互换的计数（fg.TbEcoItem），身份单独记在这份清单里：
    /// 哪一件被送进解析台，就按先来先用取走同种类最早的一条身份（面板里点名送的取点名的那条）；取消时放回。
    /// 掉落体系（FG8-LOOT-01～03）用 <see cref="Regions.HomeValleyAnalysis.Acquire"/> 发放物品时一并登记。
    /// </summary>
    [Serializable]
    public sealed class EnemyItemTagRecord
    {
        public string TagId;
        public string ItemId;
        public string TargetId;
        public string Origin;
        public long AcquiredTick;
    }

    /// <summary>FG5-RND-02：解析台 2.0 的家园侧状态（在 <see cref="ResearchState.Analysis"/>）。唯一写入口 <see cref="Regions.HomeValleyAnalysis"/>。</summary>
    [Serializable]
    public sealed class AnalysisBenchState
    {
        /// <summary>家园里敌方物品的身份清单（按取得先后）。</summary>
        public EnemyItemTagRecord[] Tags = Array.Empty<EnemyItemTagRecord>();
        public int NextSerial = 1;
        /// <summary>FGR-RND-024：送进解析台、等着空闲时处理的残骸件数。</summary>
        public int WreckBuffer;
        /// <summary>当前这件残骸已处理的游戏秒（缺电 / 队列有活时停住，不清零）。</summary>
        public float WreckProgress;
        /// <summary>数据核心首次解析读到的资料（fg.TbAnalysisLore.id，按读到的先后）。</summary>
        public string[] LoreRead = Array.Empty<string>();
        /// <summary>统计：处理过的残骸、解析与残骸各产出的技术数据、完成的解析项数。</summary>
        public long WrecksProcessed;
        public long TechFromAnalysis;
        public long TechFromWrecks;
        public long Completed;
        /// <summary>“第一次拿到敌方物品”的引导钩子已经为这个存档触发过（钩子本身按玩家只广播一次，这里只防同一存档每次读档重复扫描）。</summary>
        public bool FirstItemSeen;
    }

    /// <summary>
    /// FG5-RND-03：靶场的存档部分（在 <see cref="ResearchState.Range"/>）。唯一写入口 <see cref="Economy.TestRangeService"/>。
    /// **仿真投影与正在进行的测试不在这里**（FGR-RND-031“投影不会进入存档”）：存档前正在进行的测试直接结束、结果记进 <see cref="History"/>，
    /// 读档后每座靶场都是空闲状态。这里只存玩家的设置与已经结束的测试结果。
    /// </summary>
    [Serializable]
    public sealed class TestRangeState
    {
        public int DomainVersion = 1;
        /// <summary>FGR-RND-030：击败过的敌人类型（fg.TbMechEnemy.id，按 ID 排序）——对应的阵营靶子据此解锁。</summary>
        public string[] DefeatedEnemyTypes = Array.Empty<string>();
        /// <summary>每座靶场的靶子布置（按建筑 ID 排序）；建筑没了随之清掉。</summary>
        public RangeLayoutRecord[] Layouts = Array.Empty<RangeLayoutRecord>();
        /// <summary>卡片“靶子布置的预设”（FG05 第 4 节）：跨靶场共用，至多 range.presets.max 个。</summary>
        public RangePresetRecord[] Presets = Array.Empty<RangePresetRecord>();
        public int NextPresetSerial = 1;
        /// <summary>FGR-RND-032：已经结束的测试结果（新的在后，至多 range.history.keep 条），两次结果可以并排对比。</summary>
        public RangeResultRecord[] History = Array.Empty<RangeResultRecord>();
        public int NextResultSerial = 1;
        /// <summary>玩家已经看过的靶子类型（面板里新解锁的靶子标“新”，打开过一次就清掉）。</summary>
        public string[] SeenTargets = Array.Empty<string>();
        /// <summary>对比栏选中的两条结果序号（0 = 自动：最近两条）。</summary>
        public int CompareA;
        public int CompareB;
        /// <summary>统计：做过的测试次数、接入投影的次数。</summary>
        public long TestsRun;
        public long UplinksRun;
    }

    /// <summary>FG5-RND-03：一座靶场的靶子布置——每个靶位放什么（fg.TbRangeTarget.id；空串 = 空位）。</summary>
    [Serializable]
    public sealed class RangeLayoutRecord
    {
        public string BuildingId;
        public string[] Slots = Array.Empty<string>();
    }

    /// <summary>FG5-RND-03：靶子布置预设（名字 + 每个靶位放什么）。</summary>
    [Serializable]
    public sealed class RangePresetRecord
    {
        public string PresetId;
        public string Name = string.Empty;
        public string[] Slots = Array.Empty<string>();
    }

    /// <summary>FG5-RND-03（FGR-RND-032）：一条反应 / 一个标签的计数（反应 = 触发次数；标签 = 覆盖率千分比）。</summary>
    [Serializable]
    public sealed class RangeCountRecord
    {
        public string Id;
        public int Value;
    }

    /// <summary>FG5-RND-03（FGR-RND-032）：一次测试的读数。</summary>
    [Serializable]
    public sealed class RangeResultRecord
    {
        public int Serial;
        public string BuildingId;
        public long StartTick;
        public long EndTick;
        /// <summary>测试时长（游戏秒）。</summary>
        public float Seconds;
        /// <summary>参加测试的投影（显示名，按加入先后；“·接入”= 测试中被接入过）。</summary>
        public string[] Projections = Array.Empty<string>();
        /// <summary>测试时的靶子布置（每个靶位的 fg.TbRangeTarget.id，空串 = 空位）。</summary>
        public string[] Targets = Array.Empty<string>();
        /// <summary>对投影靶造成的总伤害（内核精确值）与每秒伤害。</summary>
        public float Damage;
        public float Dps;
        public int Kills;
        public int Shots;
        /// <summary>能耗：生效固件每发耗电（表 power 列）× 发数的合计。</summary>
        public float Energy;
        public float PeakHeat;
        public int Overheats;
        /// <summary>各反应的触发次数（反应 ID，按次数从多到少）。</summary>
        public RangeCountRecord[] Reactions = Array.Empty<RangeCountRecord>();
        /// <summary>各状态标签的覆盖率（标签 ID → 千分比：采样时刻挂着这个标签的投影靶占比的平均）。</summary>
        public RangeCountRecord[] Coverage = Array.Empty<RangeCountRecord>();
        /// <summary>热量曲线：按采样间隔记的投影最高热量（至多 range.curve.points 个点，超出时等距抽样）。</summary>
        public float[] HeatCurve = Array.Empty<float>();
        public float CurveStepSeconds;
        /// <summary>结束原因（<see cref="Economy.RangeEndReason"/>）。</summary>
        public int EndReason;
        public bool Uplinked;
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
