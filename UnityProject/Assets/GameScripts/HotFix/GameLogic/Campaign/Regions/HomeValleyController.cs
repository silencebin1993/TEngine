using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER2-SCENE-01：归还谷地空间/持久化流程。不是 <see cref="GameLogic.Stage.IStageFlow"/>——
    /// <see cref="GameLogic.Stage.StageId"/> 枚举（Cell/Organ/Creature/...）是产品宏观演化阶段骨架，
    /// 归还谷地是 Cell 时代内的一个"战略基地区域"，与远征战斗（未来复用 CellStageFlow 的 SimBridge
    /// 内核）是并列关系，不应该塞进同一个枚举维度。<see cref="GameLogic.Stage.GameRoot"/> 直接持有
    /// 并驱动本类，和 HUD host 是同一种"director 之外的常驻子系统"处理方式。
    ///
    /// 范围边界（2026-09-21 用户裁决"填完再结"）：建筑修复/残骸拆解的资源事务+电网仲裁+
    /// 点选移动+到点自动干活，均在本类内实现并实测（AC-JRN-002 的"可工作"journey 端到端可玩）。
    /// 勘查确认 <c>CellPlayerController</c>（单一意识附体换人模型，`Bind` 要求 StatSheet/
    /// AbilitySystem/ResourceWallet 这类玩家进化属性）与"多台平等工作机器"语义不符，不能直接复用；
    /// 因此移动走独立的 Transform 插值（<see cref="HomeValleyMachineMarker.CommandMoveTo"/>），
    /// 不接 SimBridge——避免在没把握的情况下往共享战斗内核里加东西。统一 Direct/Strategy/
    /// Transition/Modal 输入域表、WASD 直控换乘、真正的镜头接管仍是 ER2-INPUT-01 的范围，
    /// E 交互按钮/UI 面板仍是 ER5-INT-01/UI-04 的范围（DIGEST 早已登记，不是本 Story 新开的口子）。</summary>
    public sealed class HomeValleyController
    {
        public bool IsActive { get; private set; }

        private GameObject _root;
        private Camera _camera;
        private readonly List<HomeValleyMachineMarker> _machineMarkers = new List<HomeValleyMachineMarker>(4);
        private HomeValleyMachineMarker _selected;
        private readonly Dictionary<string, float> _repairRemainingSeconds = new Dictionary<string, float>(3);
        private readonly Dictionary<string, float> _salvageRemainingSeconds = new Dictionary<string, float>(2);

        /// <summary>本类由 <see cref="GameLogic.Stage.GameRoot"/> 用 <c>??=</c> 惰性创建、跨多局
        /// 复用同一实例（同一进程内先后玩过 A、B 两局）。<see cref="_repairRemainingSeconds"/> 等运行时
        /// 计时字典是纯内存态、不落盘、按 <see cref="BuildingRecord.BuildingId"/> 这个在任何战役里都
        /// 长得一样的字符串做键——不清空的话，A 局一个没跑完的维修计时会被 B 局的同名建筑误认成
        /// "已有订单在进行中"（实测发现，真实 bug，不是假设）。用 CampaignId 判断是否真的换了一局。</summary>
        private string _boundCampaignId;

        public void Enter(bool resume)
        {
            if (IsActive)
            {
                Log.Warning("[HomeValleyController] Enter 被重复调用，忽略（已处于激活状态）。");
                return;
            }

            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                Log.Error("[HomeValleyController] 没有活动战役（CampaignSession.Current 为空），拒绝进入归还谷地。");
                return;
            }

            if (_boundCampaignId != state.CampaignId)
            {
                _repairRemainingSeconds.Clear();
                _salvageRemainingSeconds.Clear();
                _boundCampaignId = state.CampaignId;
            }

            EnsureRegionSeeded(state);
            EnsureMachinesSeeded(state);
            state.CurrentRegionId = HomeValleyLayout.RegionId;
            RecomputePower(state); // 幂等：新建战役刚播种、或读档恢复旧存档，都用当前数据重算一次。
            RearmInterruptedWorkOrders(state);

            BuildVisuals(state);
            SetupCamera();

            IsActive = true;

            SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete);
            if (!saveResult.Success)
            {
                Log.Warning($"[HomeValleyController] HomeEntryComplete 自动存档未成功：" +
                    $"{saveResult.Outcome} {saveResult.Message}");
            }

            Log.Info($"[HomeValleyController] 已进入归还谷地（resume={resume}），" +
                $"建筑 {CountRegionBuildings(state)} 项，机器 {_machineMarkers.Count} 台。");
        }

        public void Update(float dt)
        {
            if (!IsActive)
            {
                return;
            }
            HandleSelectionClick();
            TickMachineMovement(dt);
            TickRepairs(dt);
            TickSalvage(dt);
        }

        private void TickMachineMovement(float dt)
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                marker.Tick(dt);
            }
        }

        /// <summary>退出归还谷地（进远征/回主菜单）。只清理本区域的运行时表现，
        /// <see cref="MachineRegistry"/>/<see cref="CampaignState"/> 记录原样保留——
        /// 这就是"持久化"要求：回城/再次读档要看到同一批建筑与残骸状态，不是重新生成。</summary>
        public void Exit()
        {
            if (!IsActive)
            {
                return;
            }

            SyncLiveStateBackToRecords();
            DestroyVisuals();
            IsActive = false;
            Log.Info("[HomeValleyController] 已退出归还谷地。");
        }

        // ── 首次进入播种 / 持久化 ────────────────────────────────────────────

        private static bool TryFindRegion(CampaignState state, out RegionRecord region)
        {
            region = state.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId);
            return region != null;
        }

        private static int CountRegionBuildings(CampaignState state)
        {
            return state.BuildingRecords?.Count(b => b.RegionId == HomeValleyLayout.RegionId) ?? 0;
        }

        /// <summary>首次进入才建立 RegionRecord + 7 条 BuildingRecord；已存在则原样跳过——
        /// 这是"第二次进入/读档不重复对象"负向用例的核心保证。</summary>
        private static void EnsureRegionSeeded(CampaignState state)
        {
            if (TryFindRegion(state, out _))
            {
                return;
            }

            var region = new RegionRecord
            {
                RegionId = HomeValleyLayout.RegionId,
                State = RegionState.Active,
                DiscoveredNodes = new[] { HomeValleyLayout.Wreckage1NodeId, HomeValleyLayout.Wreckage2NodeId },
                DestroyedNodeIds = Array.Empty<string>(),
                LootedContainerIds = Array.Empty<string>(),
                LostQuestSalvageIds = Array.Empty<string>(),
                EnemyAlertLevel = 0f,
                AdaptationId = null,
                ExpeditionCount = 0,
                CoreGateUnlocked = false,
                CoreState = null,
            };
            state.RegionRecords = (state.RegionRecords ?? Array.Empty<RegionRecord>())
                .Append(region).ToArray();

            var buildings = new List<BuildingRecord>
            {
                NewBuilding(HomeValleyLayout.Core, BuildingConstructionState.Operational,
                    BuildingPowerState.Powered, health: 500f),
                NewBuilding(HomeValleyLayout.Generator, BuildingConstructionState.Damaged,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                NewBuilding(HomeValleyLayout.Warehouse, BuildingConstructionState.Damaged,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                NewBuilding(HomeValleyLayout.SignalTower, BuildingConstructionState.Damaged,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                // PowerState 先给 NotApplicable 占位——EnsureRegionSeeded 返回后 Enter() 立即调用
                // RecomputePower，会按 ERD-ECO-002 的容量/优先级仲裁重新计算成 Powered/Brownout。
                NewBuilding(HomeValleyLayout.AssemblyStation, BuildingConstructionState.Operational,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                NewBuilding(HomeValleyLayout.AnalysisBench, BuildingConstructionState.Operational,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                NewBuilding(HomeValleyLayout.RepairBay, BuildingConstructionState.Operational,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
            };
            state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>())
                .Concat(buildings).ToArray();

            // DEMO-CONTENT-LOCK.md §2.2：核心基础电力 20、带宽 3。真实电网容量/需求聚合与脏重算
            // 属于 ER3-PWR-01（MILESTONES.md 第 143 行），本 Story 只落这两个"核心天生自带"的数值，
            // 不在这里计算 PowerDemand 聚合。
            state.PowerCapacity = 20f;
            state.SignalBandwidth = 3f;

            Log.Info("[HomeValleyController] 首次进入归还谷地：已播种 RegionRecord + 7 条建筑记录。");
        }

        /// <summary>建筑没有内容锁定表专属 HP 数值（只有维修材料/时间），暂用中性占位；
        /// 等建筑受损/战斗机制的 Story 落地时再替换成真实数值，不影响本 Story 的
        /// Damaged/Operational 可区分表现要求。</summary>
        private const float PlaceholderStructureHealth = 100f;

        private static BuildingRecord NewBuilding(
            HomeValleyLayout.Anchor anchor,
            BuildingConstructionState constructionState,
            BuildingPowerState powerState,
            float health)
        {
            HomeValleyLayout.PowerProfile.TryGetValue(anchor.Id, out (float PowerDemand, int PowerPriority) profile);
            return new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + anchor.Id,
                BuildingTypeId = anchor.Id,
                RegionId = HomeValleyLayout.RegionId,
                Position = anchor.Position,
                Rotation = 0f,
                Health = health,
                ConstructionState = constructionState,
                PowerPriority = profile.PowerPriority,
                PowerState = powerState,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
                BlockedReason = null,
            };
        }

        /// <summary>首次进入才登记 ERC-001/002；已有记录（新战役当局已生成，或读档已恢复）时
        /// 原样复用，绝不重复 SpawnMachine——否则每次回城都会多出一台机器（AC-LIFE-001/002）。</summary>
        private static void EnsureMachinesSeeded(CampaignState state)
        {
            SpawnIfMissing(HomeValleyLayout.Erc001Spawn);
            SpawnIfMissing(HomeValleyLayout.Erc002Spawn);

            void SpawnIfMissing(HomeValleyLayout.Anchor spawn)
            {
                bool exists = MachineRegistry.AllRecords.Any(r =>
                    r.RegionId == HomeValleyLayout.RegionId && r.ChassisId == spawn.Id && r.IsAlive);
                if (exists)
                {
                    return;
                }

                MachineOpResult result = MachineRegistry.SpawnMachine(
                    chassisId: spawn.Id,
                    blueprintId: "placeholder:" + spawn.Id,
                    regionId: HomeValleyLayout.RegionId,
                    position: spawn.Position,
                    health: 100f,
                    maxHealth: 100f);

                if (!result.Success)
                {
                    Log.Error($"[HomeValleyController] 登记机器 {spawn.Id} 失败：{result.Error} {result.Message}");
                }
            }
        }

        private void SyncLiveStateBackToRecords()
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker == null)
                {
                    continue;
                }
                Vector3 p = marker.transform.position;
                float health = MachineRegistry.TryGetRecord(marker.LogicId, out MachineRecord rec) ? rec.Health : 100f;
                MachineRegistry.SyncLiveState(marker.LogicId, new Vector2(p.x, p.z), health, null);
            }
        }

        // ── 资源事务 / WorkOrder 修复 / 残骸拆解（DEBT-ER2SCENE01-01 收口）─────

        public readonly struct RepairStartResult
        {
            public readonly bool Success;
            public readonly string Message;
            private RepairStartResult(bool success, string message) { Success = success; Message = message; }
            public static RepairStartResult Ok(string message) => new RepairStartResult(true, message);
            public static RepairStartResult Fail(string message) => new RepairStartResult(false, message);
        }

        /// <summary>核心"应急缓存180/180"是 <see cref="CampaignState.Scrap"/> 的封顶展示，不是独立
        /// 容器——ERD-ECO-001 完整的多容器资源事务模型（核心缓存/仓库/机器货舱互相转移、总量守恒）
        /// 属于 ER3-ECO-01；本 Story 只保证"180"这个数字来源可追溯、不双记账。</summary>
        public static (int Current, int Cap) GetCoreCacheDisplay(CampaignState state)
        {
            const int cap = 180;
            return (Mathf.Clamp(state.Scrap, 0, cap), cap);
        }

        /// <summary>对 Damaged 建筑发起正式修复：立即按 <see cref="HomeValleyLayout.RepairProfile"/>
        /// 扣废料（DEMO-CONTENT-LOCK.md §2.1"初始缓存与仓库同走事务API"——扣款是事务的即时半段，
        /// 完工是另一半，这里用"扣款即时+完工计时"实现，不做可取消的中途退款，因为本表所有修复都
        /// 没有"可取消"用例）、登记一条 <see cref="WorkOrderRecord"/>，交给 <see cref="TickRepairs"/>
        /// 计时完工。触发方式（点建筑/E 交互）由 ER5-INT-01/UI-04 负责调用本方法，本类不做输入绑定。</summary>
        public RepairStartResult TryStartRepair(string buildingTypeId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || !IsActive)
            {
                return RepairStartResult.Fail("没有活动的归还谷地会话。");
            }
            if (!HomeValleyLayout.RepairProfile.TryGetValue(buildingTypeId, out (int ScrapCost, float Seconds) profile))
            {
                return RepairStartResult.Fail($"{buildingTypeId} 不在修复清单里（无需修复材料前置或不存在）。");
            }
            BuildingRecord building = FindBuilding(state, buildingTypeId);
            if (building == null)
            {
                return RepairStartResult.Fail($"归还谷地没有 {buildingTypeId} 建筑记录。");
            }
            if (building.ConstructionState != BuildingConstructionState.Damaged)
            {
                return RepairStartResult.Fail($"{buildingTypeId} 当前是 {building.ConstructionState}，不是 Damaged，无需修复。");
            }
            if (_repairRemainingSeconds.ContainsKey(building.BuildingId))
            {
                return RepairStartResult.Fail($"{buildingTypeId} 已有维修订单在进行中。");
            }
            if (state.Scrap < profile.ScrapCost)
            {
                return RepairStartResult.Fail($"废料不足：需要 {profile.ScrapCost}，当前 {state.Scrap}。");
            }

            state.Scrap -= profile.ScrapCost;
            var order = new WorkOrderRecord
            {
                WorkOrderId = building.BuildingId + ":repair:" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = WorkOrderKind.Repair,
                IssuerId = "player",
                TargetId = building.BuildingId,
                SourceId = null,
                DestinationId = null,
                RequiredTags = Array.Empty<string>(),
                ResourceTransactionId = null,
                Priority = 0,
                CreatedTick = 0,
                AssignedMachineLogicId = 0,
                State = WorkOrderState.InProgress,
                FailureReason = null,
                RetryCount = 0,
            };
            state.WorkOrders = (state.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Append(order).ToArray();
            _repairRemainingSeconds[building.BuildingId] = profile.Seconds;

            Log.Info($"[HomeValleyController] 开始修复 {buildingTypeId}：扣废料 {profile.ScrapCost}，预计 {profile.Seconds}s。");
            return RepairStartResult.Ok(order.WorkOrderId);
        }

        /// <summary>进程重启（真实读档，不是同进程 Exit/Enter 往返）后，内存计时器天然是空的——
        /// <see cref="WorkOrderRecord"/>（ERD-WRK-001）当前没有 Duration/Progress 字段能还原"还剩多少
        /// 秒"，这是记录类型本身的缺口（完整状态机是 ER3-WRK-01/02 的范围）。本方法用"整段时长重新计时"
        /// 兜底，保证卡在 InProgress 的订单能继续走完而不是永久卡死——重启前的部分进度会被重置，
        /// 不是精确续期，但优于永久软锁。</summary>
        private void RearmInterruptedWorkOrders(CampaignState state)
        {
            if (state.WorkOrders == null)
            {
                return;
            }

            foreach (WorkOrderRecord order in state.WorkOrders)
            {
                if (order.State != WorkOrderState.InProgress || order.Kind != WorkOrderKind.Repair)
                {
                    continue;
                }
                if (_repairRemainingSeconds.ContainsKey(order.TargetId))
                {
                    continue;
                }
                BuildingRecord building = state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == order.TargetId);
                if (building == null || building.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                if (!HomeValleyLayout.RepairProfile.TryGetValue(building.BuildingTypeId, out (int ScrapCost, float Seconds) profile))
                {
                    continue;
                }

                _repairRemainingSeconds[order.TargetId] = profile.Seconds;
                Log.Warning($"[HomeValleyController] 重启后发现未完工的维修订单 {order.WorkOrderId}，" +
                    $"按完整时长 {profile.Seconds}s 重新计时（不精确续期，见方法注释）。");
            }
        }

        private void TickRepairs(float dt)
        {
            if (_repairRemainingSeconds.Count == 0)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }

            var buildingIds = new List<string>(_repairRemainingSeconds.Keys);
            foreach (string buildingId in buildingIds)
            {
                float remaining = _repairRemainingSeconds[buildingId] - dt;
                if (remaining > 0f)
                {
                    _repairRemainingSeconds[buildingId] = remaining;
                    continue;
                }
                _repairRemainingSeconds.Remove(buildingId);
                CompleteRepair(state, buildingId);
            }
        }

        private void CompleteRepair(CampaignState state, string buildingId)
        {
            BuildingRecord building = state.BuildingRecords.FirstOrDefault(b => b.BuildingId == buildingId);
            if (building == null)
            {
                return;
            }

            building.ConstructionState = BuildingConstructionState.Operational;

            if (building.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator)
            {
                state.PowerCapacity += 80f; // DEMO-CONTENT-LOCK.md §2.1：修复后额外电力 80。
            }
            else if (building.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower)
            {
                state.SignalBandwidth += 5f; // §2.1：额外带宽 5，开放破碎都市（区域解锁属于 ER5 范围）。
            }
            // 仓库：只转 Operational。容量 300 是仓储守恒（ERD-ECO-003）的字段范畴，
            // 属于 ER3-STO-01——本 Story 不新增未经该 Story 定义的容量字段。

            WorkOrderRecord order = state.WorkOrders?.LastOrDefault(w =>
                w.TargetId == buildingId && w.State == WorkOrderState.InProgress);
            if (order != null)
            {
                order.State = WorkOrderState.Completed;
            }

            RecomputePower(state);
            RefreshBuildingVisual(building);
            Log.Info($"[HomeValleyController] {buildingId} 修复完成，转 Operational。");
        }

        /// <summary>拆解残骸（DEMO-CONTENT-LOCK.md §2.1：一次性 60 废料，12 秒）。与修复不同——
        /// 完工才发废料，不是即时扣款，因为这是产出而非消耗事务。</summary>
        public RepairStartResult TryStartSalvage(string nodeId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || !IsActive)
            {
                return RepairStartResult.Fail("没有活动的归还谷地会话。");
            }
            if (nodeId != HomeValleyLayout.Wreckage1NodeId && nodeId != HomeValleyLayout.Wreckage2NodeId)
            {
                return RepairStartResult.Fail($"{nodeId} 不是归还谷地的残骸节点。");
            }
            RegionRecord region = state.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId);
            if (region == null)
            {
                return RepairStartResult.Fail("归还谷地区域记录不存在。");
            }
            if (region.DestroyedNodeIds.Contains(nodeId))
            {
                return RepairStartResult.Fail($"{nodeId} 已经拆解过了。");
            }
            if (_salvageRemainingSeconds.ContainsKey(nodeId))
            {
                return RepairStartResult.Fail($"{nodeId} 已有拆解任务在进行中。");
            }

            _salvageRemainingSeconds[nodeId] = HomeValleyLayout.WreckageDismantleSeconds;
            Log.Info($"[HomeValleyController] 开始拆解 {nodeId}，预计 {HomeValleyLayout.WreckageDismantleSeconds}s。");
            return RepairStartResult.Ok(nodeId);
        }

        private void TickSalvage(float dt)
        {
            if (_salvageRemainingSeconds.Count == 0)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }

            var nodeIds = new List<string>(_salvageRemainingSeconds.Keys);
            foreach (string nodeId in nodeIds)
            {
                float remaining = _salvageRemainingSeconds[nodeId] - dt;
                if (remaining > 0f)
                {
                    _salvageRemainingSeconds[nodeId] = remaining;
                    continue;
                }
                _salvageRemainingSeconds.Remove(nodeId);
                CompleteSalvage(state, nodeId);
            }
        }

        private void CompleteSalvage(CampaignState state, string nodeId)
        {
            RegionRecord region = state.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId);
            if (region == null || region.DestroyedNodeIds.Contains(nodeId))
            {
                return;
            }

            region.DestroyedNodeIds = region.DestroyedNodeIds.Append(nodeId).ToArray();
            state.Scrap += HomeValleyLayout.WreckageScrapYield;

            Transform wreckageGo = _root != null ? _root.transform.Find("Wreckage_" + nodeId) : null;
            if (wreckageGo != null)
            {
                UnityEngine.Object.Destroy(wreckageGo.gameObject);
            }

            Log.Info($"[HomeValleyController] {nodeId} 拆解完成，+{HomeValleyLayout.WreckageScrapYield} 废料。");
        }

        /// <summary>ERD-ECO-002 电网仲裁的最小实现：按优先级（数字小优先）+ buildingId 稳定排序
        /// 确定性分配 <see cref="CampaignState.PowerCapacity"/>；分不到的 Operational 消费者进
        /// Brownout（与 DEMO-IMPLEMENTATION-SPEC.md ERD-ECO-002 原文一致）。只在容量/建筑状态变化后
        /// 调用（Enter/修复完工），不逐帧重算——满足"热更层每帧不得 O(建筑数)"的性能纪律。
        /// 只覆盖归还谷地目前存在的建筑类型；跨区域/信标加入后的更完整仲裁属于 ER3-PWR-01。</summary>
        private static void RecomputePower(CampaignState state)
        {
            List<BuildingRecord> consumers = state.BuildingRecords
                .Where(b => b.RegionId == HomeValleyLayout.RegionId
                    && b.ConstructionState == BuildingConstructionState.Operational
                    && HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId))
                .OrderBy(b => b.PowerPriority)
                .ThenBy(b => b.BuildingId, StringComparer.Ordinal)
                .ToList();

            float remaining = state.PowerCapacity;
            float totalDemand = 0f;
            foreach (BuildingRecord building in consumers)
            {
                float need = HomeValleyLayout.PowerProfile[building.BuildingTypeId].PowerDemand;
                totalDemand += need;
                if (remaining >= need)
                {
                    building.PowerState = BuildingPowerState.Powered;
                    remaining -= need;
                }
                else
                {
                    building.PowerState = BuildingPowerState.Brownout;
                }
            }
            state.PowerDemand = totalDemand;
        }

        private static BuildingRecord FindBuilding(CampaignState state, string buildingTypeId)
        {
            return state.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == buildingTypeId);
        }

        private void RefreshBuildingVisual(BuildingRecord building)
        {
            if (_root == null)
            {
                return;
            }
            Transform go = _root.transform.Find("Building_" + building.BuildingTypeId);
            Renderer renderer = go != null ? go.GetComponent<Renderer>() : null;
            if (renderer != null)
            {
                renderer.material.color = ColorForBuilding(building);
            }
        }

        // ── 可视化（占位几何体）────────────────────────────────────────────

        private void BuildVisuals(CampaignState state)
        {
            _root = new GameObject("[HomeValley]");
            _machineMarkers.Clear();
            _selected = null;

            foreach (BuildingRecord building in state.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId))
            {
                BuildBuildingVisual(building);
            }

            RegionRecord region = state.RegionRecords.First(r => r.RegionId == HomeValleyLayout.RegionId);
            if (!region.DestroyedNodeIds.Contains(HomeValleyLayout.Wreckage1NodeId))
            {
                BuildWreckageVisual(HomeValleyLayout.Wreckage1);
            }
            if (!region.DestroyedNodeIds.Contains(HomeValleyLayout.Wreckage2NodeId))
            {
                BuildWreckageVisual(HomeValleyLayout.Wreckage2);
            }

            BuildBeaconSlotVisual();

            foreach (MachineRecord machine in MachineRegistry.AllRecords.Where(m =>
                m.RegionId == HomeValleyLayout.RegionId && m.IsAlive))
            {
                BuildMachineVisual(machine);
            }
        }

        private void BuildBuildingVisual(BuildingRecord building)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Building_" + building.BuildingTypeId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(building.Position.x, 1f, building.Position.y);
            go.transform.localScale = new Vector3(3f, 2f, 3f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = ColorForBuilding(building) };
        }

        private void BuildWreckageVisual(HomeValleyLayout.Anchor wreckage)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Wreckage_" + wreckage.Id;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(wreckage.Position.x, 0.5f, wreckage.Position.y);
            go.transform.localScale = new Vector3(2.5f, 0.5f, 2.5f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = new Color(0.45f, 0.35f, 0.25f) };
        }

        private void BuildBeaconSlotVisual()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "BeaconSlot_Reserved";
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(HomeValleyLayout.BeaconSlot.Position.x, 0.05f,
                HomeValleyLayout.BeaconSlot.Position.y);
            go.transform.localScale = new Vector3(HomeValleyLayout.BeaconSlot.ClearanceRadius * 2f, 0.1f,
                HomeValleyLayout.BeaconSlot.ClearanceRadius * 2f);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.5f, 0.8f, 0.4f) };
        }

        private void BuildMachineVisual(MachineRecord machine)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Machine_" + machine.ChassisId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(machine.WorldPosition.x, 1f, machine.WorldPosition.y);
            go.transform.localScale = new Vector3(1f, 1f, 1f);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = new Color(0.7f, 0.75f, 0.8f);
            renderer.material = new Material(Shader.Find("Standard")) { color = baseColor };

            HomeValleyMachineMarker marker = go.AddComponent<HomeValleyMachineMarker>();
            marker.Initialize(machine.LogicId, machine.ChassisId, renderer, baseColor);
            _machineMarkers.Add(marker);
        }

        private static Color ColorForBuilding(BuildingRecord building)
        {
            if (building.ConstructionState == BuildingConstructionState.Damaged)
            {
                return new Color(0.75f, 0.25f, 0.2f); // 红：Damaged
            }
            switch (building.PowerState)
            {
                case BuildingPowerState.Brownout:
                    return new Color(0.85f, 0.7f, 0.15f); // 黄：Brownout
                case BuildingPowerState.OutputBlocked:
                    return new Color(0.9f, 0.45f, 0.1f); // 橙：OutputBlocked
                case BuildingPowerState.Powered:
                    return new Color(0.25f, 0.7f, 0.3f); // 绿：Operational + Powered
                default:
                    return new Color(0.5f, 0.55f, 0.6f); // 灰：Operational 但未接电（Unpowered/NotApplicable）
            }
        }

        // ── 相机（静态取景，交互式平移/接管属于 ER2-INPUT-01）─────────────────

        private void SetupCamera()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera));
                go.tag = "MainCamera";
                _camera = go.GetComponent<Camera>();
            }

            _camera.orthographic = true;
            _camera.orthographicSize = HomeValleyLayout.CameraBoundsHalfExtentZ;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.05f, 0.07f, 0.10f);
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 200f;

            Vector2 focus = HomeValleyLayout.ClampToBounds(HomeValleyLayout.CameraFocusStart);
            _camera.transform.SetPositionAndRotation(
                new Vector3(focus.x, 40f, focus.y),
                Quaternion.Euler(90f, 0f, 0f));
        }

        // ── 机器选择 / 点选移动 / 到点自动干活 ────────────────────────────────

        /// <summary>左键点机器＝选中；已选中时左键点建筑/残骸＝下令走过去、到点自动
        /// 修复/拆解；点空地＝纯移动。这是归还谷地范围内的最小 RTS 式"选中+下令"，
        /// 不是 ER2-INPUT-01 要交付的统一输入域表——见类注释范围边界。</summary>
        private void HandleSelectionClick()
        {
            if (_camera == null || !Input.GetMouseButtonDown(0))
            {
                return;
            }

            Ray ray = _camera.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 500f))
            {
                return;
            }

            HomeValleyMachineMarker marker = hit.collider.GetComponent<HomeValleyMachineMarker>();
            if (marker != null)
            {
                _selected?.SetSelected(false);
                _selected = marker;
                _selected.SetSelected(true);
                return;
            }

            if (_selected == null)
            {
                return;
            }

            HomeValleyMachineMarker moving = _selected;
            Vector3 destination = hit.point;

            string buildingTypeId = BuildingTypeIdFromHit(hit);
            if (buildingTypeId != null)
            {
                moving.CommandMoveTo(destination, () =>
                {
                    RepairStartResult r = TryStartRepair(buildingTypeId);
                    if (!r.Success)
                    {
                        Log.Warning($"[HomeValleyController] 到达 {buildingTypeId} 后无法开始修复：{r.Message}");
                    }
                });
                return;
            }

            string wreckageNodeId = WreckageNodeIdFromHit(hit);
            if (wreckageNodeId != null)
            {
                moving.CommandMoveTo(destination, () =>
                {
                    RepairStartResult r = TryStartSalvage(wreckageNodeId);
                    if (!r.Success)
                    {
                        Log.Warning($"[HomeValleyController] 到达 {wreckageNodeId} 后无法开始拆解：{r.Message}");
                    }
                });
                return;
            }

            moving.CommandMoveTo(destination);
        }

        private static string BuildingTypeIdFromHit(RaycastHit hit)
        {
            const string prefix = "Building_";
            string name = hit.collider.gameObject.name;
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : null;
        }

        private static string WreckageNodeIdFromHit(RaycastHit hit)
        {
            const string prefix = "Wreckage_";
            string name = hit.collider.gameObject.name;
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : null;
        }

        private void DestroyVisuals()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
            _machineMarkers.Clear();
            _selected = null;
        }

        // ── 自检（ER2-SCENE-01 负向矩阵，供 execute_code / 自动化验收直接断言）───

        /// <summary>负向矩阵自检："第二次进入/读档重复对象"——建筑/机器记录数必须恰好等于
        /// 播种数量，多次 Enter/Exit 循环不产生重复条目。</summary>
        public static List<string> SelfCheckNoDuplicates(CampaignState state)
        {
            var violations = new List<string>();
            int buildingCount = CountRegionBuildings(state);
            if (buildingCount != 7)
            {
                violations.Add($"归还谷地建筑记录数应为 7，实际 {buildingCount}（疑似重复播种）。");
            }

            int erc001 = MachineRegistry.AllRecords.Count(r =>
                r.RegionId == HomeValleyLayout.RegionId && r.ChassisId == HomeValleyLayout.Erc001ChassisId);
            int erc002 = MachineRegistry.AllRecords.Count(r =>
                r.RegionId == HomeValleyLayout.RegionId && r.ChassisId == HomeValleyLayout.Erc002ChassisId);
            if (erc001 != 1)
            {
                violations.Add($"ERC-001 机器记录数应为 1，实际 {erc001}。");
            }
            if (erc002 != 1)
            {
                violations.Add($"ERC-002 机器记录数应为 1，实际 {erc002}。");
            }

            return violations;
        }
    }
}
