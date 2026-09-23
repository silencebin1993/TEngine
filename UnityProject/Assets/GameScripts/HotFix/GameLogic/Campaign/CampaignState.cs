using System;
using System.Linq;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>ER1-SAVE-01：ERD-DAT-001 CampaignState 版本化骨架。全 public 字段（非属性）以兼容
    /// <see cref="JsonUtility"/>，与仓库既有的 <c>ControlHandoffSaveData</c>/<c>BlueprintSaveData</c>
    /// 同一约定。字段集合严格对齐 DEMO-IMPLEMENTATION-SPEC.md ERD-DAT-001 必须字段表，额外只加了
    /// <see cref="ObjectiveRecords"/>（STORY-EXECUTION-CARDS.md 明确点名要求，ERD-DAT-001 本身只列出
    /// 派生的 <see cref="CompletedObjectiveIds"/>）。教学显示历史不写入本类型。
    ///
    /// 本 Story 只交付骨架：大多数集合字段（BuildingRecords/MachineRecords/WorkOrders/FactoryQueues/
    /// RegionRecords 等）在新局时为空数组，真实产出数据由 ER3/ER4/ER5 各自 Story 负责写入，不在本 Story
    /// 范围内伪造。</summary>
    [Serializable]
    public sealed class CampaignState
    {
        /// <summary>存档 payload 内容自身的 schema 版本，与外层 <c>CampaignSaveEnvelope.SchemaVersion</c>
        /// 保持同步写入（两处都存是为了在 envelope 头损坏时仍能从 payload 侧核对，见
        /// <see cref="CampaignSaveService"/> 的读档校验顺序）。</summary>
        public int SchemaVersion = CampaignSaveService.CurrentSchemaVersion;

        public string CampaignId;
        public int ContentVersion = CampaignSaveService.CurrentContentVersion;
        public string DifficultyId = "Standard";
        public int RandomSeed;
        public float PlaySeconds;
        public string CurrentRegionId;
        public CampaignPhase CampaignPhase = CampaignPhase.Landing;

        public int Scrap;
        public int TechData;
        public float PowerCapacity;
        public float PowerDemand;
        public float SignalBandwidth;
        public float SignalExposure;
        /// <summary>ER7-BEACON-01：信标启动二次确认后进入10秒不可取消演出的起始 PlaySeconds，
        /// <c>-1</c>＝当前未在演出中（**不能用 0 当"未开始"哨兵**——新战役极早期就把信标造好并启动是
        /// 合法边界情况，此时 <see cref="CampaignState.PlaySeconds"/> 可能恰好还是 0，用 0 当哨兵会
        /// 把"刚在0秒启动"误判成"从未启动"，本 Story 实测踩过这个坑）。唯一写入口
        /// <see cref="Regions.HomeValleyBeacon.TryStartLaunch"/>，演出结束
        /// （<see cref="Regions.HomeValleyBeacon.Tick"/> 判定）后授予
        /// <see cref="CampaignObjectiveTracker.BeaconLaunchEventId"/> 一次性事件，不重置本字段
        /// （已启动过是永久事实，同其余一次性标记同一纪律，用 EventLedger 判"是否已授予"而不是靠
        /// 这个时间戳本身的值域推断状态）。</summary>
        public float BeaconLaunchStartedAtPlaySeconds = -1f;
        /// <summary>ER7-CREDITS-01：玩家主动接管机器的累计次数（"接管次数"结算统计项）。唯一写入口
        /// <see cref="Regions.RegionControlSystem.TrySwitchControlledUnit"/> 成功分支——与该类既有的
        /// 纯内存 <see cref="Regions.RegionControlSystem.SwitchCount"/>（每个区域控制器各自一份、
        /// 切场即清零，此前没有任何调用方消费）是两件独立的事：那个字段留给调试/单区域内瞬时计数，
        /// 这个字段才是跨区域、跨读档累加、真正落盘的战役级统计。死亡回弹自动重指向（
        /// <see cref="Regions.RegionControlSystem"/> 内部的"受控机阵亡……自动切换"分支）不计入——
        /// 不是玩家主动发起的接管。</summary>
        public int TotalControlTakeovers;

        public string[] CompletedObjectiveIds = Array.Empty<string>();
        public string[] UnlockedContentIds = Array.Empty<string>();

        public BuildingRecord[] BuildingRecords = Array.Empty<BuildingRecord>();
        public MachineRecord[] MachineRecords = Array.Empty<MachineRecord>();
        public BlueprintRecord[] BlueprintRecords = Array.Empty<BlueprintRecord>();
        public WorkOrderRecord[] WorkOrders = Array.Empty<WorkOrderRecord>();
        public FactoryQueueItemRecord[] FactoryQueues = Array.Empty<FactoryQueueItemRecord>();
        public RegionRecord[] RegionRecords = Array.Empty<RegionRecord>();
        public EventLedgerEntry[] EventLedger = Array.Empty<EventLedgerEntry>();

        /// <summary>ER3-ECO-01：ERD-DAT-005 资源事务账本。骨架字段本身随 ER1-SAVE-01 落地
        /// （<see cref="ResourceTransactionRecord"/> 定义在 CampaignRecords.cs），本 Story 起
        /// 由 <see cref="CampaignEconomyLedger"/> 唯一写入口写入真实数据；建造/生产/维修/改造/
        /// 解析奖励一律经该类而非直接改 <see cref="Scrap"/>/<see cref="TechData"/>/
        /// <see cref="PowerCapacity"/> 字段。</summary>
        public ResourceTransactionRecord[] ResourceTransactions = Array.Empty<ResourceTransactionRecord>();

        /// <summary>ER3-STO-01：ERD-ECO-003 地面物——不属于任何仓库/机器货舱的第三类存放位置，
        /// 见 <see cref="GroundItemRecord"/> 类注释。真实读写入口是
        /// <see cref="GameLogic.Campaign.Regions.HomeValleyCargo"/>，不在别处直接改本数组。</summary>
        public GroundItemRecord[] GroundItems = Array.Empty<GroundItemRecord>();

        /// <summary>ER4-PRIM-03：ERD-PRM-003 战役唯一基元芯片实例账（8 格仓+草稿+待领取三态），
        /// 唯一写入口 <see cref="GameLogic.Campaign.Primitive.PrimitiveInventory"/>。新战役为空数组，
        /// 由 <c>PrimitiveInventory.EnsureSeeded</c> 首次进入归还谷地时播种（开局8格+1件聚焦镜），
        /// 不在 <see cref="CreateNew"/> 里直接写死初始内容——与 <see cref="BlueprintRecords"/> 的既有
        /// "骨架 vs 播种"分工一致（播种入口幂等，可重复调用不重复生成）。</summary>
        public PrimitiveChipRecord[] PrimitiveChips = Array.Empty<PrimitiveChipRecord>();

        /// <summary>ER4-PRIM-04：ERD-PRM-004 合成台唯一队列，唯一写入口
        /// <see cref="GameLogic.Campaign.Primitive.PrimitiveCraftStation"/>，与 <see cref="FactoryQueues"/>
        /// 同一"单 Running 工位 FIFO"结构模式。新战役为空数组，不预先生成任何队列项。</summary>
        public CraftQueueItemRecord[] CraftQueues = Array.Empty<CraftQueueItemRecord>();

        /// <summary>ER4-PRIM-05：归还谷地范围内的低威胁残骸靶（<see cref="CombatTargetRecord"/>），
        /// 唯一写入口 <see cref="GameLogic.Campaign.Regions.HomeValleyCombatTargets"/>。新战役为空数组，
        /// 由该类 <c>EnsureSeeded</c> 首次进入归还谷地时播种，与 <see cref="PrimitiveChips"/> 同一
        /// "骨架 vs 播种"分工。</summary>
        public CombatTargetRecord[] CombatTargets = Array.Empty<CombatTargetRecord>();

        /// <summary>ER5-REGION-01：远征区域敌方/节点实例（<see cref="RegionEnemyRecord"/>），
        /// 唯一写入口 <see cref="GameLogic.Campaign.Regions.FracturedCityRegion"/>。新战役为空数组，
        /// 由该类 <c>EnsureSeeded</c> 首次进入破碎都市时播种，与 <see cref="CombatTargets"/> 同一
        /// "骨架 vs 播种"分工。</summary>
        public RegionEnemyRecord[] RegionEnemies = Array.Empty<RegionEnemyRecord>();

        /// <summary>ER5-REGION-01：远征区域关键任务物实例（<see cref="RegionQuestItemRecord"/>），
        /// 唯一写入口同上。新战役为空数组，只在节点摧毁/终端读取那一刻才真正生成第一条记录，不预先
        /// 播种（与 <see cref="RegionEnemies"/> 不同——关键物"是否存在"本身就是玩家行为的产物）。</summary>
        public RegionQuestItemRecord[] RegionQuestItems = Array.Empty<RegionQuestItemRecord>();

        /// <summary>ER6-ANA-01：解析台队列（<see cref="AnalysisQueueItemRecord"/>），唯一写入口
        /// <see cref="GameLogic.Campaign.Regions.HomeValleyAnalysis"/>。新战役为空数组，只在玩家真正
        /// 把一件已带回的模块送去解析台那一刻才生成第一条记录，同 <see cref="RegionQuestItems"/> 一样
        /// 不预先播种。</summary>
        public AnalysisQueueItemRecord[] AnalysisQueues = Array.Empty<AnalysisQueueItemRecord>();

        /// <summary>ER6-EXPOSE-01：信号暴露事件明细（HUD"来源"展示唯一权威来源），唯一写入口
        /// <see cref="CampaignExposureLedger"/>。每笔一次性事件（幂等经 <see cref="CampaignEventLedger"/>），
        /// 不循环覆盖、不合并——"净值−7但不合并成神秘数值"（摧毁监听节点同时产生两笔）是这个数组
        /// 存在的直接原因。新战役为空数组。</summary>
        public SignalExposureEventRecord[] SignalExposureEvents = Array.Empty<SignalExposureEventRecord>();

        /// <summary>ER6-EXPOSE-01：家园信号塔"主动关闭广播"玩家开关（与断电/未修复的
        /// <see cref="BuildingConstructionState"/>/<see cref="BuildingPowerState"/> 是完全独立的另一维度——
        /// 塔本身可以是 Operational+Powered，玩家仍可以主动选择不广播换取暴露下降）。唯一写入口
        /// <see cref="CampaignExposureLedger.SetTowerBroadcastOff"/>。</summary>
        public bool SignalTowerBroadcastOff;

        /// <summary>ER6-EXPOSE-01："家园关闭信号塔主动广播时每10秒-2"的累计计时器——达到10秒重置
        /// 为0并结算一笔暴露事件，唯一写入口 <see cref="CampaignExposureLedger.TickTowerBroadcastOff"/>。</summary>
        public float TowerBroadcastOffElapsedSeconds;

        /// <summary>ER6-EXPOSE-01："一次远征中每累计30秒直控+5"的累计计时器——按区域/当前远征次数
        /// 归零（见 <see cref="RegionRecord.DirectControlAccumulatedSeconds"/>），这里只是全局递增
        /// 序号，为每次跨越30秒生成不重复的 eventId（"重复规则"不依赖这个序号本身的值，只依赖它
        /// 单调递增，供 <see cref="CampaignExposureLedger.TickDirectControlExposure"/> 使用）。</summary>
        public int DirectControlExposureGrantCount;

        /// <summary>ER6-EXPOSE-01：暴露30阈值"静默侦察提示"允许重复触发（降到阈值下再升高可再次
        /// 触发），单调递增供每次穿越生成不重复的 eventId。</summary>
        public int ScoutTipCrossCount;

        public ControlHandoffRecord ControlHandoff = new ControlHandoffRecord();
        public SaveReason LastSaveReason = SaveReason.NewCampaign;

        /// <summary>额外字段（非 ERD-DAT-001 必须表原文，见类注释）。</summary>
        public ObjectiveRecord[] ObjectiveRecords = Array.Empty<ObjectiveRecord>();

        /// <summary>ER1-ID-01：<see cref="GameLogic.Campaign.MachineRegistry"/> 的 LogicId 分配器状态。
        /// 与"死亡机器记录永久保留在 <see cref="MachineRecords"/> 里"配合，理论上光靠扫描现有记录的
        /// 最大 LogicId 也能重建分配器起点，但显式持久化这两个值更稳妥：不依赖"记录永不被物理删除"
        /// 这条隐含假设，未来若有存档瘦身/裁剪也不会让 LogicId 出现复用。</summary>
        public int NextMachineLogicId = 1;

        /// <summary>同上，DisplayNumber 分配器状态。DisplayNumber 是给玩家看的编号，允许未来被
        /// 重新编排（不像 LogicId 那样有"永不复用"的硬约束），这里先按同样单调递增的口径落盘。</summary>
        public int NextMachineDisplayNumber = 1;

        /// <summary>新建战役：ERD-ECO-001 的 Demo 初始废料基线（180）已在设计文档给出数值，
        /// 直接采用；其余经济/工作/建筑/区域集合按 Done 定义留空，等待 ER3/ER4/ER5 写入真实数据。</summary>
        public static CampaignState CreateNew(string campaignId, string difficultyId, int randomSeed)
        {
            return new CampaignState
            {
                SchemaVersion = CampaignSaveService.CurrentSchemaVersion,
                CampaignId = campaignId,
                ContentVersion = CampaignSaveService.CurrentContentVersion,
                DifficultyId = string.IsNullOrEmpty(difficultyId) ? "Standard" : difficultyId,
                RandomSeed = randomSeed,
                PlaySeconds = 0f,
                CurrentRegionId = null,
                CampaignPhase = CampaignPhase.Landing,
                Scrap = 180,
                TechData = 0,
                PowerCapacity = 0f,
                PowerDemand = 0f,
                SignalBandwidth = 0f,
                SignalExposure = 0f,
                CompletedObjectiveIds = Array.Empty<string>(),
                UnlockedContentIds = Array.Empty<string>(),
                BuildingRecords = Array.Empty<BuildingRecord>(),
                MachineRecords = Array.Empty<MachineRecord>(),
                BlueprintRecords = Array.Empty<BlueprintRecord>(),
                WorkOrders = Array.Empty<WorkOrderRecord>(),
                FactoryQueues = Array.Empty<FactoryQueueItemRecord>(),
                RegionRecords = Array.Empty<RegionRecord>(),
                EventLedger = Array.Empty<EventLedgerEntry>(),
                ResourceTransactions = Array.Empty<ResourceTransactionRecord>(),
                GroundItems = Array.Empty<GroundItemRecord>(),
                PrimitiveChips = Array.Empty<PrimitiveChipRecord>(),
                CraftQueues = Array.Empty<CraftQueueItemRecord>(),
                CombatTargets = Array.Empty<CombatTargetRecord>(),
                RegionEnemies = Array.Empty<RegionEnemyRecord>(),
                RegionQuestItems = Array.Empty<RegionQuestItemRecord>(),
                AnalysisQueues = Array.Empty<AnalysisQueueItemRecord>(),
                SignalExposureEvents = Array.Empty<SignalExposureEventRecord>(),
                SignalTowerBroadcastOff = false,
                TowerBroadcastOffElapsedSeconds = 0f,
                DirectControlExposureGrantCount = 0,
                ScoutTipCrossCount = 0,
                ControlHandoff = new ControlHandoffRecord(),
                LastSaveReason = SaveReason.NewCampaign,
                ObjectiveRecords = Array.Empty<ObjectiveRecord>(),
                NextMachineLogicId = 1,
                NextMachineDisplayNumber = 1,
            };
        }

        /// <summary>STORY-EXECUTION-CARDS.md #ER1-SAVE-01："序列化前对稳定 ID 排序"——
        /// Dictionary 迭代/未排序列表不得影响结算（ERD-SAV-004 确定性的前置条件）。
        /// <see cref="CampaignSaveService.Save"/> 落盘前必调用，不依赖调用方记得排序。</summary>
        public void NormalizeForSave()
        {
            CompletedObjectiveIds = (CompletedObjectiveIds ?? Array.Empty<string>())
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            UnlockedContentIds = (UnlockedContentIds ?? Array.Empty<string>())
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            BuildingRecords = (BuildingRecords ?? Array.Empty<BuildingRecord>())
                .OrderBy(r => r.BuildingId, StringComparer.Ordinal).ToArray();
            MachineRecords = (MachineRecords ?? Array.Empty<MachineRecord>())
                .OrderBy(r => r.LogicId).ToArray();
            BlueprintRecords = (BlueprintRecords ?? Array.Empty<BlueprintRecord>())
                .OrderBy(r => r.BlueprintId, StringComparer.Ordinal).ToArray();
            WorkOrders = (WorkOrders ?? Array.Empty<WorkOrderRecord>())
                .OrderBy(r => r.WorkOrderId, StringComparer.Ordinal).ToArray();
            FactoryQueues = (FactoryQueues ?? Array.Empty<FactoryQueueItemRecord>())
                .OrderBy(r => r.QueueItemId, StringComparer.Ordinal).ToArray();
            RegionRecords = (RegionRecords ?? Array.Empty<RegionRecord>())
                .OrderBy(r => r.RegionId, StringComparer.Ordinal).ToArray();
            EventLedger = (EventLedger ?? Array.Empty<EventLedgerEntry>())
                .OrderBy(r => r.EventId, StringComparer.Ordinal).ToArray();
            ResourceTransactions = (ResourceTransactions ?? Array.Empty<ResourceTransactionRecord>())
                .OrderBy(r => r.TransactionId, StringComparer.Ordinal).ToArray();
            GroundItems = (GroundItems ?? Array.Empty<GroundItemRecord>())
                .OrderBy(r => r.GroundItemId, StringComparer.Ordinal).ToArray();
            ObjectiveRecords = (ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                .OrderBy(r => r.ObjectiveId, StringComparer.Ordinal).ToArray();
            PrimitiveChips = (PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
                .OrderBy(r => r.PartId, StringComparer.Ordinal).ToArray();
            CraftQueues = (CraftQueues ?? Array.Empty<CraftQueueItemRecord>())
                .OrderBy(r => r.QueueItemId, StringComparer.Ordinal).ToArray();
            CombatTargets = (CombatTargets ?? Array.Empty<CombatTargetRecord>())
                .OrderBy(r => r.TargetId, StringComparer.Ordinal).ToArray();
            RegionEnemies = (RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                .OrderBy(r => r.EnemyInstanceId, StringComparer.Ordinal).ToArray();
            RegionQuestItems = (RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
                .OrderBy(r => r.SalvageInstanceId, StringComparer.Ordinal).ToArray();
            AnalysisQueues = (AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>())
                .OrderBy(r => r.QueueItemId, StringComparer.Ordinal).ToArray();
            SignalExposureEvents = (SignalExposureEvents ?? Array.Empty<SignalExposureEventRecord>())
                .OrderBy(r => r.EventId, StringComparer.Ordinal).ToArray();
        }
    }
}
