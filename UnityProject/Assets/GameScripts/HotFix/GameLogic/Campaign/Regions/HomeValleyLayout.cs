using System;
using System.Collections.Generic;
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

        // ── 机器 ID（DEMO-CONTENT-LOCK.md §2.1，与旧细胞阶段 chassis_ally_* 占位彻底区分）──
        public const string Erc001ChassisId = "erc_001";
        public const string Erc002ChassisId = "erc_002";
        /// <summary>默认战斗履带（DEMO-CONTENT-LOCK.md 行53），由 ER4-FAC-01 装配站生产，
        /// 归还谷地本身不出生这台机器——这里先落货位数据，ER3-STO-01 出发校验/货位计算
        /// 需要引用这个 ID，不等 ER4-FAC-01 落地才补。</summary>
        public const string Erc003ChassisId = "erc_003";

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
