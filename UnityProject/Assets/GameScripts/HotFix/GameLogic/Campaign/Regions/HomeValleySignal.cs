using System;
using System.Linq;
using TEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-SIG-01 STORY-EXECUTION-CARDS.md 第1条："信号塔修复40废料/15秒后带宽3→8，
    /// 破碎都市从Locked→Available……塔停电时带宽回落并显示当前使用/容量/超额请求原因"。
    ///
    /// ── 范围裁剪（有 REQUIREMENT-TO-PLAYABLE-TRACE.md 文本依据，非本 Story 自行裁剪） ──
    /// 卡片同时点名的 AC-CTL-002/004（接管失败码九类/Suspended 宽限回弹）实际映射在
    /// REQUIREMENT-TO-PLAYABLE-TRACE.md 到 <c>ER5-CTL-01</c>；AC-EXP-001（出征人数/带宽/装备校验）
    /// 映射到 <c>ER5-EXP-01</c>；两者均排在本 Story 之后（STORY-BOARD.md #30/#31 一带），且都需要
    /// 尚不存在的系统（真正的"接管候选/失联判定"、真正的"远征出发"）才有实际发生场景。本 Story
    /// 只交付这两个未来系统将要消费的"信号"数据层与家园侧真实可测的部分：区域解锁 + 带宽容量真实
    /// 计算与展示。"接管范围计算"/"超距/受干扰失败码"/"Suspended 回弹"/"带中继实测"随
    /// DEBT-ER5SIG01-01 移交对应 Story，不在本类臆造一个没有真实调用方的"范围"数字。
    ///
    /// ── 带宽容量的唯一真相 ──
    /// <see cref="CampaignState.SignalBandwidth"/> 已由 <see cref="HomeValleyPowerGrid.Recompute"/>
    /// 正确计算（基础3 + 信号塔 Operational 且 Powered 时的额外5，断电立即回落）——本类不重新计算，
    /// 只新增"区域解锁判定"与"HUD 展示查询"两件此前缺失的事。</summary>
    public static class HomeValleySignal
    {
        /// <summary>DEMO-CONTENT-LOCK.md 行11："`silent_ruins` 破碎都市……信号塔 Operational 且
        /// ERC-003 已生产"。</summary>
        public const string SilentRuinsRegionId = "silent_ruins";

        private const string UnlockEventId = "region_unlock:" + SilentRuinsRegionId;

        /// <summary>首次进入归还谷地才播种 Locked 态的破碎都市区域记录；已存在（第二次进入/读档）
        /// 原样跳过——与 <see cref="HomeValleyCombatTargets.EnsureSeeded"/> 同一幂等纪律。区域本身的
        /// 物件/布局属于 ER5-REGION-01，这里只先落"这个区域存在且当前 Locked"这一事实。</summary>
        public static void EnsureSeeded(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            state.RegionRecords ??= Array.Empty<RegionRecord>();
            if (state.RegionRecords.Any(r => r.RegionId == SilentRuinsRegionId))
            {
                return;
            }

            var region = new RegionRecord
            {
                RegionId = SilentRuinsRegionId,
                State = RegionState.Locked,
                DiscoveredNodes = Array.Empty<string>(),
                DestroyedNodeIds = Array.Empty<string>(),
                LootedContainerIds = Array.Empty<string>(),
                LostQuestSalvageIds = Array.Empty<string>(),
                EnemyAlertLevel = 0f,
                AdaptationId = null,
                ExpeditionCount = 0,
                CoreGateUnlocked = false,
                CoreState = null,
            };
            state.RegionRecords = state.RegionRecords.Append(region).ToArray();
        }

        public static RegionRecord Find(CampaignState state) =>
            state?.RegionRecords?.FirstOrDefault(r => r.RegionId == SilentRuinsRegionId);

        /// <summary>逐帧重判解锁条件（家园侧建筑/机器数量个位数，O(1) 量级，不违反热更层性能纪律）。
        /// 条件满足前保持 Locked，不臆造中间态；条件一旦满足就是终态（不会因为信号塔之后又停电而
        /// 重新锁上——"解锁"是玩家已经达成过的历史事实，同 <see cref="RegionState"/> 其余状态机
        /// 单调推进的既定语义，见 <see cref="ERD-EXP-001"/> RegionRecord.state 定义"）。返回 true 表示
        /// 本次调用真正触发了解锁（供调用方一次性反馈：HUD 提示/日志），false 表示条件未满足或早已
        /// 解锁过。见 ERD-EXP-001 RegionRecord.state 定义（DEMO-IMPLEMENTATION-SPEC.md）。</summary>
        public static bool RecomputeUnlock(CampaignState state)
        {
            RegionRecord region = Find(state);
            if (region == null || region.State != RegionState.Locked)
            {
                return false;
            }

            bool towerOperational = state.BuildingRecords?.Any(b =>
                b.RegionId == HomeValleyLayout.RegionId &&
                b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower &&
                b.ConstructionState == BuildingConstructionState.Operational) ?? false;
            if (!towerOperational)
            {
                return false;
            }

            bool hasErc003 = MachineRegistry.AllRecords.Any(m =>
                m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId == HomeValleyLayout.Erc003ChassisId);
            if (!hasErc003)
            {
                return false;
            }

            region.State = RegionState.Available;
            CampaignEventLedger.TryGrant(state, UnlockEventId, "RegionUnlock", state.PlaySeconds, SilentRuinsRegionId);
            Log.Info("[HomeValleySignal] 破碎都市（silent_ruins）已解锁：信号塔 Operational 且 ERC-003 已生产。");
            return true;
        }

        /// <summary>HUD 只读查询：当前信号带宽容量（唯一真相仍是 <see cref="HomeValleyPowerGrid.Recompute"/>
        /// 写入的 <see cref="CampaignState.SignalBandwidth"/>，本方法不重新计算）。</summary>
        public static float BandwidthCapacity(CampaignState state) => state?.SignalBandwidth ?? 0f;

        /// <summary>HUD 只读查询：信号塔当前是否正在贡献额外带宽（用于展示"基础3 + 塔5"来源，
        /// 断电/未修复时只显示基础值）。</summary>
        public static bool TowerContributing(CampaignState state)
        {
            return state?.BuildingRecords?.Any(b =>
                b.RegionId == HomeValleyLayout.RegionId &&
                b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower &&
                b.ConstructionState == BuildingConstructionState.Operational &&
                b.PowerState == BuildingPowerState.Powered) ?? false;
        }
    }
}
