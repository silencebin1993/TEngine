using System;
using System.Collections.Generic;
using GameLogic.Campaign.Content;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER2-SCENE-01：归还谷地的空间锚点表。所有坐标是 <see cref="Vector2"/>
    /// （x/z 地面坐标，映射到世界的 X/Z 轴，Y 恒为地面高度 0），全部用 Transform 坐标常量定义，
    /// 不写死进任何地形网格或美术资产——2026-09-21 用户裁决：三张地图地形走 PCG 后期生成，
    /// 正式低模走混元+功能美术绑定管线，均不在本 Story 范围；本表只保证空间关系（可达、不重叠、
    /// 相机边界）在换真实地形/美术后不需要重写。</summary>
    public static class HomeValleyLayout
    {
        /// <summary>DEMO-CONTENT-LOCK.md §1：`home_valley` 区域 ID，与 <see cref="RegionRecord.RegionId"/>
        /// / <see cref="MachineRecord.RegionId"/> 保持完全一致的字符串。</summary>
        public const string RegionId = "home_valley";

        // ── 建筑类型 ID（内部键，纯机械命名，不含 org_/gene_/cell_ 等禁用词前缀）──────────
        public const string BuildingTypeCore = "core";
        public const string BuildingTypeGenerator = "generator";
        public const string BuildingTypeWarehouse = "warehouse";
        public const string BuildingTypeSignalTower = "signal_tower";
        public const string BuildingTypeAssemblyStation = "assembly_station";
        public const string BuildingTypeAnalysisBench = "analysis_bench";
        public const string BuildingTypeRepairBay = "repair_bay";
        /// <summary>ER3-WRK-01 Build 工作单的唯一真实内容（DEMO-CONTENT-LOCK.md §2.2："额外建造第二座
        /// 发电机（60废料、40秒、+80容量)"）。独立 BuildingTypeId（不复用 <see cref="BuildingTypeGenerator"/>）
        /// 只是为了不破坏既有"Building_"+typeId 的可视化/点选命名约定（两者共用同一发电机语义，
        /// <see cref="PowerSupplyProfile"/> 各自登记 80，<see cref="HomeValleyPowerGrid.Recompute"/> 按
        /// Operational 建筑逐条求和，天然支持同时存在两座发电机，不需要改电网仲裁代码）。</summary>
        public const string BuildingTypeGenerator2 = "generator_2";

        // ── 机器 ID（DEMO-CONTENT-LOCK.md §2.1，与旧细胞阶段 chassis_ally_* 占位彻底区分）──
        public const string Erc001ChassisId = "erc_001";
        public const string Erc002ChassisId = "erc_002";
        /// <summary>默认战斗履带（DEMO-CONTENT-LOCK.md 行53），由 ER4-FAC-01 装配站生产，
        /// 归还谷地本身不出生这台机器——这里先落货位数据，ER3-STO-01 出发校验/货位计算
        /// 需要引用这个 ID，不等 ER4-FAC-01 落地才补。</summary>
        public const string Erc003ChassisId = "erc_003";
        /// <summary>ER3-SOFTLOCK-01 AC-ECO-011："核心紧急重启搬运机"——家园经济彻底卡死（无正式
        /// 搬运机且废料/装配站都指望不上）时核心自动打印的受限单位，只能 Repair（见
        /// <see cref="HomeValleyWorkOrders"/> 的 ChassisCapabilities），不参与正常机队编号/生产统计。
        /// 见 <see cref="HomeValleySoftlockGuard"/>。</summary>
        public const string ErcRescueChassisId = "erc_rescue";

        /// <summary>单个空间锚点：位置 + 最小净空半径，供 <see cref="Validate"/> 做不重叠/可达性检查。</summary>
        public readonly struct Anchor
        {
            public readonly string Id;
            public readonly Vector2 Position;
            public readonly float ClearanceRadius;

            public Anchor(string id, Vector2 position, float clearanceRadius)
            {
                Id = id;
                Position = position;
                ClearanceRadius = clearanceRadius;
            }
        }

        // ── 建筑锚点（DEMO-CONTENT-LOCK.md §1/2.1/2.2）───────────────────────────────
        public static readonly Anchor Core = new Anchor(BuildingTypeCore, new Vector2(0f, 0f), 4f);
        public static readonly Anchor Generator = new Anchor(BuildingTypeGenerator, new Vector2(14f, 14f), 3f);
        public static readonly Anchor Warehouse = new Anchor(BuildingTypeWarehouse, new Vector2(20f, 0f), 3.5f);
        public static readonly Anchor WarehouseExit = new Anchor("warehouse_exit", new Vector2(20f, 8f), 1.5f);
        public static readonly Anchor SignalTower = new Anchor(BuildingTypeSignalTower, new Vector2(-14f, 14f), 3f);
        public static readonly Anchor AssemblyStation = new Anchor(BuildingTypeAssemblyStation, new Vector2(0f, -20f), 3.5f);
        public static readonly Anchor AssemblyExit = new Anchor("assembly_exit", new Vector2(8f, -20f), 1.5f);
        public static readonly Anchor AnalysisBench = new Anchor(BuildingTypeAnalysisBench, new Vector2(-10f, -20f), 3f);
        public static readonly Anchor RepairBay = new Anchor(BuildingTypeRepairBay, new Vector2(16f, -14f), 3f);
        /// <summary>第二座发电机建造位（ER3-WRK-01 Build）。与全部既有锚点净空不重叠，见
        /// <see cref="Validate"/> 自检——(24,-8) 半径3：距仓库(20,0,r3.5)约8.9、距维修台(16,-14,r3)约10、
        /// 距装配出口(8,-20,r1.5)约20，均留有余量；未建成前只是一处可点选的空地占位（"BuildSite_" +
        /// <see cref="BuildingTypeGenerator2"/>），不预先生成 <see cref="BuildingRecord"/>。</summary>
        public static readonly Anchor Generator2Site = new Anchor(BuildingTypeGenerator2, new Vector2(24f, -8f), 3f);

        // ── 机器出生点（DEMO-CONTENT-LOCK.md §2.1：ERC-001/002）───────────────────────
        public static readonly Anchor Erc001Spawn = new Anchor(Erc001ChassisId, new Vector2(-10f, 6f), 1.5f);
        public static readonly Anchor Erc002Spawn = new Anchor(Erc002ChassisId, new Vector2(-10f, -6f), 1.5f);

        // ── 一次性残骸节点（DEMO-CONTENT-LOCK.md §2.1：两处各 60 废料）────────────────
        public const string Wreckage1NodeId = "home_valley_wreckage_1";
        public const string Wreckage2NodeId = "home_valley_wreckage_2";
        public static readonly Anchor Wreckage1 = new Anchor(Wreckage1NodeId, new Vector2(-20f, -4f), 2f);
        public static readonly Anchor Wreckage2 = new Anchor(Wreckage2NodeId, new Vector2(-4f, 20f), 2f);
        public const int WreckageScrapYield = 60;
        public const float WreckageDismantleSeconds = 12f;

        /// <summary>信标预留位（DEMO-CONTENT-LOCK.md §2.2："解锁前不存在"）——只标记空间占位，
        /// 解锁前不生成 <see cref="BuildingRecord"/>，真正建造由 ER7-BEACON-01 接手。</summary>
        public const string BeaconSlotId = "beacon_slot";
        public static readonly Anchor BeaconSlot = new Anchor(BeaconSlotId, new Vector2(12f, 22f), 3f);

        /// <summary>相机初始聚焦点——两台初始机器与核心之间，非任意值。</summary>
        public static readonly Vector2 CameraFocusStart = new Vector2(-4f, 2f);

        /// <summary>相机矩形边界半宽/半高（世界 X/Z），覆盖全部锚点 + 安全边距。</summary>
        public const float CameraBoundsHalfExtentX = 28f;
        public const float CameraBoundsHalfExtentZ = 30f;

        /// <summary>各建筑 Operational 时的电力需求与默认优先级（DEMO-CONTENT-LOCK.md §2.2）。
        /// 玩家可在 1～4 范围内调整 <see cref="BuildingRecord.PowerPriority"/>（ER3-PWR-01
        /// <see cref="HomeValleyPowerGrid.TrySetPriority"/>），此表只是新建筑落地时的默认值。</summary>
        public static readonly IReadOnlyDictionary<string, (float PowerDemand, int PowerPriority)> PowerProfile =
            new Dictionary<string, (float, int)>
            {
                [BuildingTypeCore] = (10f, 1),
                [BuildingTypeWarehouse] = (5f, 1),
                [BuildingTypeAssemblyStation] = (25f, 2),
                [BuildingTypeAnalysisBench] = (15f, 2),
                [BuildingTypeRepairBay] = (15f, 3),
                [BuildingTypeSignalTower] = (20f, 2),
            };

        /// <summary>ER3-PWR-01：核心自带基础供电（DEMO-CONTENT-LOCK.md §2.1"核心20"），不依赖任何
        /// 建筑的 Operational 状态——发电机损坏/被关停时，这部分供给仍然存在，保证核心（demand 10）
        /// 永远不会被电网仲裁断电。<see cref="HomeValleyPowerGrid.Recompute"/> 每次都从这个常量算起，
        /// 不是一个可以被"扣减"的历史累加值。</summary>
        public const float BaseCoreSupply = 20f;

        /// <summary>ER3-PWR-01：供给类建筑（目前只有发电机）Operational 时贡献的电力供给
        /// （DEMO-CONTENT-LOCK.md §2.1"电机+80"）。与 <see cref="PowerProfile"/>（消费侧）是两张
        /// 独立的表——发电机本身不消费电力，也不在 PowerProfile 里出现。</summary>
        public static readonly IReadOnlyDictionary<string, float> PowerSupplyProfile =
            new Dictionary<string, float>
            {
                [BuildingTypeGenerator] = 80f,
                [BuildingTypeGenerator2] = 80f,
            };

        /// <summary>核心自带基础带宽（DEMO-CONTENT-LOCK.md §2.2"基础带宽 3"），不依赖信号塔状态，
        /// 与 <see cref="SignalTowerBandwidthBonus"/> 是两个独立叠加的来源。</summary>
        public const float BaseSignalBandwidth = 3f;

        /// <summary>信号塔 Operational 且实际分到电（Powered）时的额外带宽加成（§2.2"额外带宽 5"）；
        /// 断电（Brownout/Unpowered）时这部分加成不生效（ER3-PWR-01："信号塔断电同时降低带宽"）。</summary>
        public const float SignalTowerBandwidthBonus = 5f;

        /// <summary>修复成本/时长（DEMO-CONTENT-LOCK.md §2.1）。装配站/解析台/维修台无需修复材料前置，
        /// 不在此表出现。</summary>
        public static readonly IReadOnlyDictionary<string, (int ScrapCost, float Seconds)> RepairProfile =
            new Dictionary<string, (int, float)>
            {
                [BuildingTypeGenerator] = (30, 20f),
                [BuildingTypeWarehouse] = (10, 10f),
                [BuildingTypeSignalTower] = (40, 15f),
            };

        /// <summary>ER3-WRK-01 Build 工作单成本/时长（DEMO-CONTENT-LOCK.md §2.2 唯一点名的真实建造
        /// 内容——第二座发电机）。与 <see cref="RepairProfile"/> 是两张独立的表：Repair 面向"已存在但
        /// Damaged"的建筑，Build 面向"尚不存在、需要新建"的建筑，键集合故意不重叠。</summary>
        public static readonly IReadOnlyDictionary<string, (int ScrapCost, float Seconds)> BuildProfile =
            new Dictionary<string, (int, float)>
            {
                [BuildingTypeGenerator2] = (60, 40f),
            };

        /// <summary>ER3-WRK-01 Recharge：默认电池容量（DEMO-CONTENT-LOCK.md §2.4"默认电池容量：
        /// 搬运100、战斗120、维修140"）。ERC-001/002 均为搬运轮式；ERC-003 是战斗履带（见该常量旁注释）。
        /// 主/功能/维修动作消耗战术电池的完整战斗能耗模型属于 ER4-PRIM-04/战斗基元 Story（本 Story 之外
        /// 没有真实的电池消耗来源），本 Story 交付的是"被动恢复+家园充电点+完整 Recharge 状态机"这套
        /// 真实机制，可用 <see cref="MachineRecord.Battery"/> 公开字段在测试里直接调低模拟低电触发。</summary>
        public static readonly IReadOnlyDictionary<string, float> BatteryCapacity = new Dictionary<string, float>
        {
            [Erc001ChassisId] = 100f,
            [Erc002ChassisId] = 100f,
            [Erc003ChassisId] = 120f,
            [ErcRescueChassisId] = 100f,
            // ER4-CONTENT-01：维修悬浮底盘（Content.ChassisCatalog.ChassisHoverId），生产队列待
            // ER4-FAC-01，先落数据不等该 Story 落地才补（同 Erc003ChassisId 先例）。
            [ChassisCatalog.ChassisHoverId] = 140f,
        };

        /// <summary>被动恢复速率（不在充电点时也生效，DEMO-CONTENT-LOCK.md §2.4"被动恢复每秒1"）。</summary>
        public const float BatteryPassiveRegenPerSecond = 1f;

        /// <summary>家园充电点速率（DEMO-CONTENT-LOCK.md §2.4"家园有电充电点每秒10"）。Recharge
        /// 工作单在 <see cref="Core"/>（归还核心，永远 Powered，见 <see cref="HomeValleyPowerGrid"/>
        /// 类注释）进行，不新增专属充电桩建筑。</summary>
        public const float BatteryHomeChargeRatePerSecond = 10f;

        /// <summary>低电自动候选阈值（DEMO-CONTENT-LOCK.md §2.4"电池低于20%时自动候选"）——
        /// 自动候选算法本身属于 ER3-WRK-02，本 Story 只落这个阈值常量供该 Story 直接复用，
        /// 以及供玩家手动 Recharge 判断"是否真的需要充"的 HUD 展示阈值。</summary>
        public const float BatteryLowFraction = 0.2f;

        /// <summary>ERD-WRK-003 第二条："路径连续5秒无进展"判定窗口。归还谷地当前只有直线插值移动、
        /// 无真实寻路/障碍物系统（<see cref="HomeValleyMachineMarker"/> 全程必达），本 Story 按"净位移
        /// 是否推进"实现这条规则本身（看门狗式防御性代码，真实寻路阻塞场景要等未来引入 NavMesh 后
        /// 天然复用同一套状态机，不是本 Story 的缺口），用单元测试直接冻结位置驱动触发/释放来验证。</summary>
        public const float PathStallSeconds = 5f;

        /// <summary>路径卡住判定的最小净位移阈值（低于此值视为"没有推进"）。</summary>
        public const float PathStallEpsilon = 0.05f;

        /// <summary>PathBlocked 冷却窗口（ERD-WRK-003"30 秒后...重试"）——自动重试分配算法本身属于
        /// ER3-WRK-02（该 Story 的分配器会在此窗口结束后把 Ready 订单纳入候选池），本 Story 只保证
        /// 冷却结束后订单状态会从 Waiting 自动回到 Ready，可供玩家手动重新指派。</summary>
        public const float PathBlockedRetrySeconds = 30f;

        /// <summary>ER3-STO-01 ERD-ECO-003：归还核心应急缓存容量（DEMO-IMPLEMENTATION-SPEC.md ERD-ECO-003：
        /// "初始180废料存在归还核心应急缓存中，缓存是有限库存，上限180"）。恒定生效，不依赖任何建筑
        /// Operational 状态——核心缓存本身不是一栋可损坏的建筑。只接受废料，不接受远征战利品（模块/
        /// 数据盒等），见 <see cref="Regions.HomeValleyCargo.GetStorageCapacity"/>。</summary>
        public const int CoreCacheCapacity = 180;

        /// <summary>仓库修复（<see cref="BuildingTypeWarehouse"/> 转 Operational）后追加的库存容量
        /// （DEMO-IMPLEMENTATION-SPEC.md ERD-ECO-003"仓库修复后搬运机可将剩余库存转运过去"）。
        /// 与 <see cref="CoreCacheCapacity"/> 是两个独立叠加的容量来源，仓库未修复时贡献为 0。</summary>
        public const int WarehouseCapacity = 300;

        /// <summary>货物占用货位换算（DEMO-CONTENT-LOCK.md §2.3"废料每箱40占1货位"）。完整模块/
        /// 终端数据盒/核心数据各占1货位，不用这个换算——只有废料按数量/本值向上取整。</summary>
        public const int ScrapUnitsPerCargoSlot = 40;

        /// <summary>各机型基础货位（DEMO-CONTENT-LOCK.md §2.3：ERC-001=4/ERC-002=2/ERC-003=1）。
        /// 货舱结构模块（+2，最多一个，不叠加成6）属于 ER4-BLP-01 蓝图槽位范畴，本表只落每型默认值，
        /// 不在此处理蓝图改装。</summary>
        public static readonly IReadOnlyDictionary<string, int> MachineCargoSlots = new Dictionary<string, int>
        {
            [Erc001ChassisId] = 4,
            [Erc002ChassisId] = 2,
            [Erc003ChassisId] = 1,
            // ER4-CONTENT-01：维修悬浮底盘，同上先落数据。
            [ChassisCatalog.ChassisHoverId] = 1,
        };

        /// <summary>ER3-SOFTLOCK-01 AC-ECO-011 触发阈值之一："废料 &lt; 35"。35 不是随意选的数字——
        /// 对应 ER4-FAC-01 卡片点名的"搬运机 35 废料/20 秒"生产成本（STORY-EXECUTION-CARDS.md
        /// ER4-FAC-01 第2条），语义是"如果废料多到能在（未来的）装配站生产一台新搬运机，就不算真的
        /// 卡死"——装配站真实生产队列尚未落地（ER4-FAC-01），这里只借用同一个数值做判定，不等
        /// 该 Story 落地才补这条兜底。</summary>
        public const int EmergencyRescueScrapThreshold = 35;

        /// <summary>ER3-SOFTLOCK-01 AC-ECO-012：拆除非核心建筑的耗时。卡片未点名具体秒数，取和
        /// <see cref="RepairProfile"/> 同量级的中间值（介于仓库10秒和信号塔40秒之间）。</summary>
        public const float DemolishSeconds = 15f;

        // ── ER4-FAC-01：装配站默认生产蓝图（STORY-EXECUTION-CARDS.md 第2条点名的三条默认数值，
        // 与 DEMO-CONTENT-LOCK.md §2.4 行53～55 一致；ER4-BLP-01 正式电路/蓝图编辑器落地前，这是
        // 装配站面板"生产"可选的封闭三项，不是玩家可自由组合的完整蓝图系统）───────────────

        /// <summary>默认战斗履带 ERC-003（DEMO-CONTENT-LOCK.md 行53："60 废料，30 秒"）。</summary>
        public const string BlueprintErc003Id = "bp_erc003";
        /// <summary>新搬运轮式（行54："35 废料，20 秒"）。<see cref="ProduceBlueprintDefault.ChassisId"/>
        /// 复用 <see cref="Erc002ChassisId"/>——与开局 ERC-002 是同一"搬运轮式"底盘类型（多台机器共享
        /// 同一 ChassisId 取值，本就是既有约定，<see cref="Erc003ChassisId"/> 同理也会被多台战斗履带
        /// 共享），不新增第二套字符串常量，<see cref="Content.ChassisCatalog.ResolveArchetype"/> 与
        /// <see cref="HomeValleyWorkOrders"/> 的 ChassisCapabilities 表都不需要为此改动。</summary>
        public const string BlueprintHaulerId = "bp_hauler";
        /// <summary>默认维修悬浮（行55："55 废料，25 秒"），底盘取
        /// <see cref="Content.ChassisCatalog.ChassisHoverId"/>。</summary>
        public const string BlueprintHoverId = "bp_hover";

        /// <summary>单条默认生产蓝图的静态数据：底盘、展示名、出厂废料成本、生产秒数。
        /// <see cref="Regions.HomeValleyFactory"/> 用本表播种 <see cref="BlueprintRecord"/>（成本随
        /// <see cref="BlueprintVersionRecord.ScrapCost"/> 落盘、可被"版本锁定"覆盖——见该类"改蓝图
        /// activeVersion 不回溯改已排订单"注释），秒数则是本 Story 唯一权威来源，不随蓝图版本变化
        /// （当前内容锁定表没有"同一底盘不同版本耗时不同"的设计意图，避免无依据地引入第二根变化轴）。</summary>
        public readonly struct ProduceBlueprintDefault
        {
            public readonly string ChassisId;
            public readonly string DisplayName;
            public readonly int ScrapCost;
            public readonly float Seconds;

            public ProduceBlueprintDefault(string chassisId, string displayName, int scrapCost, float seconds)
            {
                ChassisId = chassisId;
                DisplayName = displayName;
                ScrapCost = scrapCost;
                Seconds = seconds;
            }
        }

        public static readonly IReadOnlyDictionary<string, ProduceBlueprintDefault> FactoryProduceDefaults =
            new Dictionary<string, ProduceBlueprintDefault>
            {
                [BlueprintErc003Id] = new ProduceBlueprintDefault(Erc003ChassisId, "战斗履带 ERC-003", 60, 30f),
                [BlueprintHaulerId] = new ProduceBlueprintDefault(Erc002ChassisId, "搬运机", 35, 20f),
                [BlueprintHoverId] = new ProduceBlueprintDefault(ChassisCatalog.ChassisHoverId, "维修机", 55, 25f),
            };

        /// <summary>全部空间锚点（含机器出生点、残骸、信标预留位），供不重叠/可达性校验遍历。</summary>
        public static IEnumerable<Anchor> AllAnchors()
        {
            yield return Core;
            yield return Generator;
            yield return Warehouse;
            yield return WarehouseExit;
            yield return SignalTower;
            yield return AssemblyStation;
            yield return AssemblyExit;
            yield return AnalysisBench;
            yield return RepairBay;
            yield return Generator2Site;
            yield return Erc001Spawn;
            yield return Erc002Spawn;
            yield return Wreckage1;
            yield return Wreckage2;
            yield return BeaconSlot;
        }

        /// <summary>把任意焦点位置夹回相机矩形边界内。ER2-SCENE-01 只交付边界数据与本方法；
        /// 交互式镜头平移/接管属于 ER2-INPUT-01（"镜头...均走 CameraDirector"），本 Story 的相机
        /// 只做静态取景，不在这里加自己的输入轮询，避免和下一个 Story 的统一输入表打架。</summary>
        public static Vector2 ClampToBounds(Vector2 focus)
        {
            return new Vector2(
                Mathf.Clamp(focus.x, -CameraBoundsHalfExtentX, CameraBoundsHalfExtentX),
                Mathf.Clamp(focus.y, -CameraBoundsHalfExtentZ, CameraBoundsHalfExtentZ));
        }

        /// <summary>ER2-SCENE-01 负向矩阵："出生点堵塞"/"残骸不可达"/"相机无锚点"的静态自检——
        /// 纯数据校验，不依赖 Play Mode。任何两个锚点的净空圆重叠、或相机边界未覆盖全部锚点，
        /// 都会被判为一条违规信息；调用方（<see cref="Stage.Homeland.HomeValleyController"/> 自检
        /// 与自动化验收）据此判定通过/失败，不做视觉走查判断。</summary>
        public static List<string> Validate()
        {
            var violations = new List<string>();
            var anchors = new List<Anchor>(AllAnchors());

            for (int i = 0; i < anchors.Count; i++)
            {
                for (int j = i + 1; j < anchors.Count; j++)
                {
                    float minGap = anchors[i].ClearanceRadius + anchors[j].ClearanceRadius;
                    float dist = Vector2.Distance(anchors[i].Position, anchors[j].Position);
                    if (dist < minGap)
                    {
                        violations.Add(
                            $"锚点 {anchors[i].Id} 与 {anchors[j].Id} 净空重叠：距离 {dist:F2} < 所需 {minGap:F2}");
                    }
                }

                Anchor a = anchors[i];
                bool insideBounds =
                    Mathf.Abs(a.Position.x) + a.ClearanceRadius <= CameraBoundsHalfExtentX &&
                    Mathf.Abs(a.Position.y) + a.ClearanceRadius <= CameraBoundsHalfExtentZ;
                if (!insideBounds)
                {
                    violations.Add($"锚点 {a.Id} 超出相机边界（半宽 {CameraBoundsHalfExtentX}/{CameraBoundsHalfExtentZ}）");
                }
            }

            if (CameraBoundsHalfExtentX <= 0f || CameraBoundsHalfExtentZ <= 0f)
            {
                violations.Add("相机边界半宽/半高必须为正数（相机无锚点）");
            }

            return violations;
        }
    }
}
