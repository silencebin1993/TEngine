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
        /// <summary>ER7-BEACON-01：导航信标（DEMO-CONTENT-LOCK.md §逐目标持久化表 OBJ-10"建造并启动
        /// 返航信标"）。解锁前不存在（<see cref="CampaignObjectiveTracker.Obj09"/> 完成才可见/可建），
        /// 建造流程与 <see cref="BuildingTypeGenerator2"/> 共用同一 <see cref="HomeValleyWorkOrders.TryCreateBuild"/>
        /// Build 工作单管线，不新造第二套建造系统。</summary>
        public const string BuildingTypeBeacon = "beacon";

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

        // ── FG0-ARCH-04（FGR-ARC-001“迁移”）：Demo 的固定锚点 → 开局布局表 fg.TbStartLayout ─────────────────
        // 坐标 = 归还核心枢轴格 + 表里的偏移（手工锚点，FG00 B25）。核心落点由世界生成决定（FG3-GEN-01），在那之前是原点，
        // 所以这些位置与 Demo 常量逐项相同；净空半径同样来自表（clearance 列），Validate 的不重叠自检照旧。
        // 建筑本身已是格网建筑（BuildingRecord.GridX/GridY/Rotation），这里的建筑锚点只给“开局布局里它原本在哪”的查询用。
        public static Anchor Core => LayoutAnchor(BuildingTypeCore);
        public static Anchor Generator => LayoutAnchor(BuildingTypeGenerator);
        public static Anchor Warehouse => LayoutAnchor(BuildingTypeWarehouse);
        public static Anchor WarehouseExit => LayoutAnchor("warehouse_exit");
        public static Anchor SignalTower => LayoutAnchor(BuildingTypeSignalTower);
        public static Anchor AssemblyStation => LayoutAnchor(BuildingTypeAssemblyStation);
        public static Anchor AssemblyExit => LayoutAnchor("assembly_exit");
        public static Anchor AnalysisBench => LayoutAnchor(BuildingTypeAnalysisBench);
        public static Anchor RepairBay => LayoutAnchor(BuildingTypeRepairBay);
        /// <summary>第二座发电机的建议建造位（开局布局 kind=site）。FG0-ARCH-04 起发电机可以在格网上自由放置、可以建多座；
        /// 这里只是 Demo 流程点“建造位”时的默认落点，放置同样经过格网校验。</summary>
        public static Anchor Generator2Site => LayoutAnchor(BuildingTypeGenerator2);

        public static Anchor Erc001Spawn => LayoutAnchor(Erc001ChassisId);
        public static Anchor Erc002Spawn => LayoutAnchor(Erc002ChassisId);

        // ── 一次性残骸节点（DEMO-CONTENT-LOCK.md §2.1：两处各 60 废料）────────────────
        public const string Wreckage1NodeId = "home_valley_wreckage_1";
        public const string Wreckage2NodeId = "home_valley_wreckage_2";
        public static Anchor Wreckage1 => LayoutAnchor(Wreckage1NodeId);
        public static Anchor Wreckage2 => LayoutAnchor(Wreckage2NodeId);
        public static int WreckageScrapYield => Grid.GridContent.TuningInt("home.wreckage_scrap_yield");
        public static float WreckageDismantleSeconds => Grid.GridContent.Tuning("home.wreckage_dismantle_seconds");

        /// <summary>ER4-PRIM-05：低威胁残骸靶——归还谷地范围内的静止命中验证目标（见 <see cref="HomeValleyCombatTargets"/>）。</summary>
        public const string LowThreatTargetAnchorId = "combat_target_low_threat";
        public static Anchor LowThreatTarget => LayoutAnchor(LowThreatTargetAnchorId);
        public static Vector2 LowThreatTargetPosition => LowThreatTarget.Position;

        /// <summary>信标的建议建造位（开局布局 kind=site）。解锁前不可建（fg.TbBuildingGrid unlockRule=beacon）。</summary>
        public const string BeaconSlotId = "beacon_slot";
        public static Anchor BeaconSlot => LayoutAnchor(BeaconSlotId);

        /// <summary>相机初始聚焦点——两台初始机器与核心之间。</summary>
        public static Vector2 CameraFocusStart => LayoutAnchor("camera_focus").Position;

        /// <summary>相机矩形边界半宽/半高（世界 X/Z，相对核心），覆盖全部锚点 + 安全边距。镜头自由平移由 FG0-ARCH-01 接手。</summary>
        public const float CameraBoundsHalfExtentX = 28f;
        public const float CameraBoundsHalfExtentZ = 30f;

        private static int _anchorRevision;
        private static readonly Dictionary<string, Anchor> AnchorCache = new Dictionary<string, Anchor>(StringComparer.Ordinal);

        /// <summary>开局布局表里的一个锚点（世界 XZ = 核心枢轴格 + 偏移）。查不到抛异常（布局表坏了不能悄悄放到原点）。</summary>
        public static Anchor LayoutAnchor(string anchorId)
        {
            if (_anchorRevision != Grid.GridContent.Revision)
            {
                AnchorCache.Clear();
                _anchorRevision = Grid.GridContent.Revision;
            }
            Grid.GridCell core = Grid.HomeGridService.CorePivot(CampaignSession.Current);
            if (!AnchorCache.TryGetValue(anchorId, out Anchor local))
            {
                GameConfig.fg.StartLayout row = Grid.GridContent.Layout(anchorId);
                local = new Anchor(row.AnchorId, new Vector2(row.OffsetX, row.OffsetY), row.Clearance);
                AnchorCache[anchorId] = local;
            }
            return new Anchor(local.Id, local.Position + new Vector2(core.X, core.Y), local.ClearanceRadius);
        }

        /// <summary>各建筑 Operational 时的电力需求与默认优先级（DEMO-CONTENT-LOCK.md §2.2）。
        /// 玩家可在 1～4 范围内调整 <see cref="BuildingRecord.PowerPriority"/>（ER3-PWR-01
        /// <see cref="HomeValleyPowerGrid.TrySetPriority"/>），此表只是新建筑落地时的默认值。</summary>
        public static IReadOnlyDictionary<string, (float PowerDemand, int PowerPriority)> PowerProfile
        {
            get
            {
                EnsureContentProfiles();
                return _powerProfile;
            }
        }

        /// <summary>ER3-PWR-01：核心自带基础供电（DEMO-CONTENT-LOCK.md §2.1"核心20"），不依赖任何
        /// 建筑的 Operational 状态——发电机损坏/被关停时，这部分供给仍然存在，保证核心（demand 10）
        /// 永远不会被电网仲裁断电。<see cref="HomeValleyPowerGrid.Recompute"/> 每次都从这个常量算起，
        /// 不是一个可以被"扣减"的历史累加值。</summary>
        public static float BaseCoreSupply => Grid.GridContent.Tuning("home.core_base_supply");

        /// <summary>ER3-PWR-01：供给类建筑（目前只有发电机）Operational 时贡献的电力供给
        /// （DEMO-CONTENT-LOCK.md §2.1"电机+80"）。与 <see cref="PowerProfile"/>（消费侧）是两张
        /// 独立的表——发电机本身不消费电力，也不在 PowerProfile 里出现。</summary>
        public static IReadOnlyDictionary<string, float> PowerSupplyProfile
        {
            get
            {
                EnsureContentProfiles();
                return _powerSupplyProfile;
            }
        }

        /// <summary>核心自带基础带宽（DEMO-CONTENT-LOCK.md §2.2"基础带宽 3"），不依赖信号塔状态，
        /// 与 <see cref="SignalTowerBandwidthBonus"/> 是两个独立叠加的来源。</summary>
        public static float BaseSignalBandwidth => Grid.GridContent.Tuning("home.base_signal_bandwidth");

        /// <summary>信号塔 Operational 且实际分到电（Powered）时的额外带宽加成（§2.2"额外带宽 5"）；
        /// 断电（Brownout/Unpowered）时这部分加成不生效（ER3-PWR-01："信号塔断电同时降低带宽"）。</summary>
        public static float SignalTowerBandwidthBonus => Grid.GridContent.Tuning("home.signal_tower_bandwidth_bonus");

        /// <summary>修复成本/时长（DEMO-CONTENT-LOCK.md §2.1）。装配站/解析台/维修台无需修复材料前置，
        /// 不在此表出现。</summary>
        public static IReadOnlyDictionary<string, (int ScrapCost, float Seconds)> RepairProfile
        {
            get
            {
                EnsureContentProfiles();
                return _repairProfile;
            }
        }

        /// <summary>ER3-WRK-01 Build 工作单成本/时长（DEMO-CONTENT-LOCK.md §2.2 唯一点名的真实建造
        /// 内容——第二座发电机）。与 <see cref="RepairProfile"/> 是两张独立的表：Repair 面向"已存在但
        /// Damaged"的建筑，Build 面向"尚不存在、需要新建"的建筑，键集合故意不重叠。</summary>
        public static IReadOnlyDictionary<string, (int ScrapCost, float Seconds)> BuildProfile
        {
            get
            {
                EnsureContentProfiles();
                return _buildProfile;
            }
        }

        // ── FG0-DATA-01（FGR-ARC-005）：以上四张档案是 Luban 表 fg.TbBuilding 的派生视图 ──────────────
        // 数值与 Demo 常量逐项一致（DEMO-CONTENT-LOCK.md §2.1/§2.2、ER7-BEACON-01），数据源改为
        // tools/cell_tables/fgdata.py。成员判定：powerDemand>0 进 PowerProfile；powerSupply>0 进
        // PowerSupplyProfile；repairSeconds>0 进 RepairProfile；buildSeconds>0 进 BuildProfile
        // （check_luban R6 保证"秒数为 0 时废料也为 0"，不会出现"有成本却不可建"的半行）。
        // 表重载 / 测试注入时 FgContentTables.Revision 变化，下一次读取按新表重建（读取本身 O(1)）。
        private static int _contentProfileRevision;
        private static Dictionary<string, (float PowerDemand, int PowerPriority)> _powerProfile;
        private static Dictionary<string, float> _powerSupplyProfile;
        private static Dictionary<string, (int ScrapCost, float Seconds)> _repairProfile;
        private static Dictionary<string, (int ScrapCost, float Seconds)> _buildProfile;

        private static void EnsureContentProfiles()
        {
            int revision = FgContentTables.Revision;
            if (_powerProfile != null && _contentProfileRevision == revision)
            {
                return;
            }
            var power = new Dictionary<string, (float PowerDemand, int PowerPriority)>();
            var supply = new Dictionary<string, float>();
            var repair = new Dictionary<string, (int ScrapCost, float Seconds)>();
            var build = new Dictionary<string, (int ScrapCost, float Seconds)>();
            foreach (GameConfig.fg.Building row in FgContentTables.Buildings)
            {
                if (row.PowerDemand > 0f)
                {
                    power[row.TypeId] = (row.PowerDemand, row.PowerPriority);
                }
                if (row.PowerSupply > 0f)
                {
                    supply[row.TypeId] = row.PowerSupply;
                }
                if (row.RepairSeconds > 0f)
                {
                    repair[row.TypeId] = (row.RepairScrap, row.RepairSeconds);
                }
                if (row.BuildSeconds > 0f)
                {
                    build[row.TypeId] = (row.BuildScrap, row.BuildSeconds);
                }
            }
            _powerProfile = power;
            _powerSupplyProfile = supply;
            _repairProfile = repair;
            _buildProfile = build;
            _contentProfileRevision = revision;
        }
        /// <summary>ER3-WRK-01 Recharge：默认电池容量（DEMO-CONTENT-LOCK.md §2.4"默认电池容量：
        /// 搬运100、战斗120、维修140"）。ERC-001/002 均为搬运轮式；ERC-003 是战斗履带（见该常量旁注释）。
        /// 主/功能/维修动作消耗战术电池的完整战斗能耗模型属于 ER4-PRIM-04/战斗基元 Story（本 Story 之外
        /// 没有真实的电池消耗来源），本 Story 交付的是"被动恢复+家园充电点+完整 Recharge 状态机"这套
        /// 真实机制，可用 <see cref="MachineRecord.Battery"/> 公开字段在测试里直接调低模拟低电触发。</summary>
        public static IReadOnlyDictionary<string, float> BatteryCapacity
        {
            get
            {
                // FG0-ARCH-04（DEBT-FG0DATA01-03）：数值入 fg.TbHomeTuning（home.battery.<底盘ID>），与 Demo 常量逐项一致。
                if (_battery == null || _batteryRevision != Grid.GridContent.Revision)
                {
                    var d = new Dictionary<string, float>(StringComparer.Ordinal);
                    foreach (string chassis in new[] { Erc001ChassisId, Erc002ChassisId, Erc003ChassisId, ErcRescueChassisId, ChassisCatalog.ChassisHoverId })
                    {
                        d[chassis] = Grid.GridContent.Tuning("home.battery." + chassis);
                    }
                    _battery = d;
                    _batteryRevision = Grid.GridContent.Revision;
                }
                return _battery;
            }
        }

        private static Dictionary<string, float> _battery;
        private static int _batteryRevision;

        /// <summary>被动恢复速率（不在充电点时也生效，DEMO-CONTENT-LOCK.md §2.4"被动恢复每秒1"）。</summary>
        public static float BatteryPassiveRegenPerSecond => Grid.GridContent.Tuning("home.battery_passive_regen_per_second");

        /// <summary>家园充电点速率（DEMO-CONTENT-LOCK.md §2.4"家园有电充电点每秒10"）。Recharge
        /// 工作单在 <see cref="Core"/>（归还核心，永远 Powered，见 <see cref="HomeValleyPowerGrid"/>
        /// 类注释）进行，不新增专属充电桩建筑。</summary>
        public static float BatteryHomeChargeRatePerSecond => Grid.GridContent.Tuning("home.battery_home_charge_per_second");

        /// <summary>低电自动候选阈值（DEMO-CONTENT-LOCK.md §2.4"电池低于20%时自动候选"）——
        /// 自动候选算法本身属于 ER3-WRK-02，本 Story 只落这个阈值常量供该 Story 直接复用，
        /// 以及供玩家手动 Recharge 判断"是否真的需要充"的 HUD 展示阈值。</summary>
        public static float BatteryLowFraction => Grid.GridContent.Tuning("home.battery_low_fraction");

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
        public static int CoreCacheCapacity => Grid.GridContent.TuningInt("home.core_cache_capacity");

        /// <summary>仓库修复（<see cref="BuildingTypeWarehouse"/> 转 Operational）后追加的库存容量
        /// （DEMO-IMPLEMENTATION-SPEC.md ERD-ECO-003"仓库修复后搬运机可将剩余库存转运过去"）。
        /// 与 <see cref="CoreCacheCapacity"/> 是两个独立叠加的容量来源，仓库未修复时贡献为 0。</summary>
        public static int WarehouseCapacity => Grid.GridContent.TuningInt("home.warehouse_capacity");

        /// <summary>货物占用货位换算（DEMO-CONTENT-LOCK.md §2.3"废料每箱40占1货位"）。完整模块/
        /// 终端数据盒/核心数据各占1货位，不用这个换算——只有废料按数量/本值向上取整。</summary>
        public static int ScrapUnitsPerCargoSlot => Grid.GridContent.TuningInt("home.scrap_units_per_cargo_slot");

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

        /// <summary>ER5-EXP-01：各机型基础信号带宽占用（DEMO-CONTENT-LOCK.md §2.1/§2.4：ERC-001=1/
        /// ERC-002=1/ERC-003=2/维修悬浮=1）。与 <see cref="Content.ComponentCatalog.StructRelayId"/>
        /// （信号中继，机体带宽需求+1，落在 <see cref="BlueprintVersionRecord.BandwidthCost"/>）是两个
        /// 独立叠加的来源——前者是"这台机器本身占多少信号带宽"（不管装什么），后者是"这份装配额外
        /// 加多少"，出征准备面板的"总带宽"校验须两者相加，不能只读其中一个（否则默认装配全部
        /// BandwidthCost=0，超带宽拦截永远不可能触发）。<see cref="ErcRescueChassisId"/> 未在此登记，
        /// 与 <see cref="MachineCargoSlots"/> 同一处理方式——它没有独立带宽值，缺省 0（该机型本就
        /// "不参与远征"人设，真正卡它的是无武器/无货位两项，不需要单独占位一个带宽数字）。</summary>
        public static readonly IReadOnlyDictionary<string, float> MachineBandwidthCost = new Dictionary<string, float>
        {
            [Erc001ChassisId] = 1f,
            [Erc002ChassisId] = 1f,
            [Erc003ChassisId] = 2f,
            [ChassisCatalog.ChassisHoverId] = 1f,
        };

        /// <summary>ER3-SOFTLOCK-01 AC-ECO-011 触发阈值之一："废料 &lt; 35"。35 不是随意选的数字——
        /// 对应 ER4-FAC-01 卡片点名的"搬运机 35 废料/20 秒"生产成本（STORY-EXECUTION-CARDS.md
        /// ER4-FAC-01 第2条），语义是"如果废料多到能在（未来的）装配站生产一台新搬运机，就不算真的
        /// 卡死"——装配站真实生产队列尚未落地（ER4-FAC-01），这里只借用同一个数值做判定，不等
        /// 该 Story 落地才补这条兜底。</summary>
        public static int EmergencyRescueScrapThreshold => Grid.GridContent.TuningInt("home.emergency_rescue_scrap_threshold");

        /// <summary>ER3-SOFTLOCK-01 AC-ECO-012：拆除非核心建筑的耗时。卡片未点名具体秒数，取和
        /// <see cref="RepairProfile"/> 同量级的中间值（介于仓库10秒和信号塔40秒之间）。</summary>
        public static float DemolishSeconds => Grid.GridContent.Tuning("home.demolish_seconds");

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

        /// <summary>ER4-PRIM-02：ERC-001（开局搬运轮式，货舱型）的默认蓝图 ID。ERC-001 不进入
        /// <see cref="FactoryProduceDefaults"/>（它是开局固定单位，不可在装配站再生产一台），但
        /// PRIMITIVE-FULL-DEMO-SPEC.md §3.1 要求所有默认机器都有自洽的电路板数据；DEMO-CONTENT-LOCK.md
        /// §2.4"货舱……ERC-001 开局即带"确认它的默认装配是纯搬运型（<see cref="Content.ComponentCatalog.StructCargoId"/>，
        /// 无主组件），因此没有可攻击的 8 号汇槽——这是设计使然而非缺口，见
        /// <see cref="Blueprint.BlueprintCircuitDefaults"/> 类注释。</summary>
        public const string BlueprintErc001Id = "bp_erc001";

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
            yield return LowThreatTarget;
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
