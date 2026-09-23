using System;
using System.Linq;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER3-SOFTLOCK-01 AC-ECO-011：家园经济彻底卡死时的兜底恢复——判定"没有正式搬运机
    /// 且废料/装配站都指望不上"，核心自动打印一台受限的紧急搬运机（<see cref="HomeValleyLayout.ErcRescueChassisId"/>，
    /// 只能 Repair，见 <see cref="HomeValleyWorkOrders"/> 的 ChassisCapabilities），交给
    /// <see cref="HomeValleyWorkOrders"/> 既有的自动分配引擎（ER3-WRK-02）自己去找一个 Damaged
    /// 建筑修复——本类不管"派它去修哪栋楼"，那是分配引擎的职责，救援机只是候选池里多了一台
    /// 只会 Repair 的机器。
    ///
    /// 判定独立于工作分配引擎（不需要玩家操作触发），由 <see cref="HomeValleyController.Update"/>
    /// 每 0.5 秒轮询一次调用（与 <see cref="HomeValleyWorkOrders"/> 的分配节奏保持一致的量级，
    /// 不是每帧都判——这几个条件的计算本身很便宜，选 0.5 秒只是为了风格统一，不是性能必需）。
    ///
    /// 核心被摧毁（<see cref="BuildingConstructionState.Destroyed"/>）目前没有真实触发源——归还谷地
    /// 没有战斗，建筑不会被打爆；<see cref="DebugDestroyCore"/> 是预留给未来战斗/围城系统的入口
    /// （同 <see cref="MachineRegistry.MarkDeadByLogicId"/> 那类"规则已实现、真实触发源尚不存在"
    /// 的既定处理方式），本 Story 只保证"一旦核心被摧毁"这条规则本身正确（不再触发救援、
    /// 家园冻结、显示失败面板），不是遗漏。</summary>
    public static class HomeValleySoftlockGuard
    {
        private const float CheckIntervalSeconds = 0.5f;
        private static float _timer;

        /// <summary>紧急机的出厂血量——比 ERC-001/002 的占位血量更脆（DEMO-CONTENT-LOCK.md 未点名
        /// 具体数值，这是"应急拼装、不是正式产线机型"的设计取舍），不影响任何验收数值。</summary>
        private const float RescueMachineHealth = 60f;

        /// <summary><paramref name="beginMovement"/>：让 <see cref="HomeValleyController"/> 对刚生成
        /// 的紧急机发起真实移动（复用玩家点选下令/ER3-WRK-02 自动分配共用的同一条移动链），本类
        /// 不直接持有 <c>HomeValleyMachineMarker</c>。</summary>
        public static void Tick(CampaignState state, float dt, Action<WorkOrderRecord> beginMovement)
        {
            _timer += dt;
            if (_timer < CheckIntervalSeconds)
            {
                return;
            }
            _timer = 0f;
            Evaluate(state, beginMovement);
        }

        /// <summary>核心是否已被摧毁——终态，一旦为真不会变回假。</summary>
        public static bool IsCoreDestroyed(CampaignState state)
        {
            BuildingRecord core = FindBuilding(state, HomeValleyLayout.BuildingTypeCore);
            return core != null && core.ConstructionState == BuildingConstructionState.Destroyed;
        }

        /// <summary>预留给未来战斗/围城系统的入口——当前没有真实调用方，仅供离线测试/Play Mode
        /// 手动验证"核心被毁→失败"这条规则本身。命名以 Debug 开头，标记它不是正式玩法入口
        /// （同工程既有的 <c>DebugForceDraft</c> 一类测试专用 API 命名约定）。</summary>
        public static void DebugDestroyCore(CampaignState state)
        {
            BuildingRecord core = FindBuilding(state, HomeValleyLayout.BuildingTypeCore);
            if (core == null)
            {
                return;
            }
            core.ConstructionState = BuildingConstructionState.Destroyed;
        }

        private static void Evaluate(CampaignState state, Action<WorkOrderRecord> beginMovement)
        {
            if (state == null || IsCoreDestroyed(state))
            {
                return; // AC-ECO-011"核心被毁则不能触发"。
            }

            if (HasLivingWorkforce(state))
            {
                return; // 还有正式搬运机，不需要救援。
            }

            if (HasLivingRescue(state))
            {
                return; // 同刻唯一：已有一台存活的紧急机，不重复生成。
            }

            bool scrapShortage = state.Scrap < HomeValleyLayout.EmergencyRescueScrapThreshold;
            bool assemblyUsable = IsAssemblyStationUsable(state);
            if (!scrapShortage && assemblyUsable)
            {
                // AC-ECO-011"废料≥35/装配站可用：提示正常再生产"——生产队列本体是 ER4-FAC-01 的范围，
                // 这里只保证不误触发紧急机；提示文案挂在哪个 UI 上是那个 Story 的事。
                return;
            }

            SpawnRescueMachine(state, beginMovement);
        }

        /// <summary>固定优先级（不依赖 Dictionary 枚举顺序）：先修发电机——它是唯一的电力供给来源，
        /// 修复它才能让电网重新给其它建筑供电，是打破整条软锁链路最关键的一环。</summary>
        private static readonly string[] RepairPriorityOrder =
        {
            HomeValleyLayout.BuildingTypeGenerator,
            HomeValleyLayout.BuildingTypeWarehouse,
            HomeValleyLayout.BuildingTypeSignalTower,
        };

        private static BuildingRecord FindMostUrgentDamagedBuilding(CampaignState state)
        {
            foreach (string typeId in RepairPriorityOrder)
            {
                BuildingRecord b = FindBuilding(state, typeId);
                if (b != null && b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    return b;
                }
            }
            return null;
        }

        private static bool HasLivingWorkforce(CampaignState state)
        {
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m.RegionId == HomeValleyLayout.RegionId && m.IsAlive
                    && (m.ChassisId == HomeValleyLayout.Erc001ChassisId || m.ChassisId == HomeValleyLayout.Erc002ChassisId))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasLivingRescue(CampaignState state)
        {
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m.RegionId == HomeValleyLayout.RegionId && m.IsAlive
                    && m.ChassisId == HomeValleyLayout.ErcRescueChassisId)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsAssemblyStationUsable(CampaignState state)
        {
            BuildingRecord assembly = FindBuilding(state, HomeValleyLayout.BuildingTypeAssemblyStation);
            return assembly != null
                && assembly.ConstructionState == BuildingConstructionState.Operational
                && assembly.PowerState == BuildingPowerState.Powered;
        }

        private static BuildingRecord FindBuilding(CampaignState state, string buildingTypeId)
        {
            return state?.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == buildingTypeId);
        }

        private static void SpawnRescueMachine(CampaignState state, Action<WorkOrderRecord> beginMovement)
        {
            MachineOpResult result = MachineRegistry.SpawnMachine(
                HomeValleyLayout.ErcRescueChassisId, "emergency:erc_rescue",
                HomeValleyLayout.RegionId, HomeValleyLayout.Core.Position, RescueMachineHealth, RescueMachineHealth);
            if (!result.Success)
            {
                Log.Warning($"[HomeValleySoftlockGuard] 紧急机生成失败：{result.Message}");
                return;
            }

            // 事件 ID 每次触发都不同（Guid 后缀）——"紧急机被毁可再触发"意味着同一战役可以多次记账，
            // TryGrant 的幂等只挡"同一个 eventId 重复调用"，不是"这类事件只能发生一次"。
            string eventId = "emergency-rescue:" + result.LogicId + ":" + Guid.NewGuid().ToString("N").Substring(0, 8);
            CampaignEventLedger.TryGrant(state, eventId, "EmergencyRescue", state.PlaySeconds);

            // ER6-EXPOSE-01 实测发现并移除的真实缺陷：此前这里有一行
            // `state.SignalExposure = Mathf.Clamp(state.SignalExposure + 10f, 0f, 100f);`——紧急救援机
            // 生成不在 DEMO-IMPLEMENTATION-SPEC.md ERD-ECO-004 点名的六类批准来源里（"暴露只由批准
            // 来源变化"，AC-EXP-005），是一条未经批准的直接写入。此前该字段从未被任何系统真正消费，
            // 这条写入没有产生过可观测后果；本 Story 起 SignalExposure 有了真实唯一写入口
            // `CampaignExposureLedger`，继续保留这行会让紧急救援机悄悄污染暴露值，已删除（不是
            // 改成走批准来源，是这条写入本身就不该存在）。

            // AC-ECO-011"执行建筑修复"：核心自己派它去修一栋 Damaged 建筑，不等玩家点选——一台只会
            // Repair、生成后没有玩家介入就永远闲置在核心旁的机器毫无意义，也无法真正打破软锁
            // （玩家此刻很可能连能操作的机器选中入口都没有，见 DEBT"无可控目标"）。免费（见
            // TryCreateEmergencyRepair），不占用本就匮乏的废料。
            BuildingRecord target = FindMostUrgentDamagedBuilding(state);
            if (target != null)
            {
                HomeValleyWorkOrders.WorkOrderOpResult repairResult =
                    HomeValleyWorkOrders.TryCreateEmergencyRepair(state, target.BuildingTypeId, result.LogicId);
                if (repairResult.Success && beginMovement != null)
                {
                    WorkOrderRecord order = HomeValleyWorkOrders.Find(state, repairResult.WorkOrderId);
                    if (order != null)
                    {
                        beginMovement(order);
                    }
                }
                else if (!repairResult.Success)
                {
                    Log.Warning($"[HomeValleySoftlockGuard] 紧急机下令修复 {target.BuildingTypeId} 失败：{repairResult.FailureReason}");
                }
            }

            Log.Info($"[HomeValleySoftlockGuard] 家园经济卡死，核心生成紧急搬运机 LogicId={result.LogicId}" +
                (target != null ? $"，已下令修复 {target.BuildingTypeId}。" : "，当前没有 Damaged 建筑可修。"));
        }
    }
}
