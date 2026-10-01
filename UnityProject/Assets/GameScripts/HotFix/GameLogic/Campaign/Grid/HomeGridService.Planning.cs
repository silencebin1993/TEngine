using System;
using GameConfig.fg;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign.Grid
{
    /// <summary>
    /// FG3-LOG-07（FGR-LOG-010 升级规划）：建筑的原地升级——与搬迁同一个唯一写入口（<see cref="HomeGridService"/>）。
    /// 升级 = 在原位放一个“升级目标”虚影（新类型、同一枢轴格与朝向、<see cref="BuildingRecord.RelocateFromId"/> 指向原建筑，ID 后缀 <see cref="UpgradeGhostSuffix"/>），
    /// 一张施工单按新旧造价的差额取料施工；完工那一刻原建筑换成新类型（设置、库存、运行状态保留）。完工前原建筑照常运转。
    /// </summary>
    public static partial class HomeGridService
    {
        /// <summary>升级目标虚影的 ID 后缀（原建筑 ID + 后缀）。</summary>
        public const string UpgradeGhostSuffix = "@up";

        /// <summary>这条记录是不是还在施工前的升级目标虚影（它同时也满足 <see cref="IsRelocationGhost"/>：共用“完工时原子换位”）。</summary>
        public static bool IsUpgradeGhost(BuildingRecord b) =>
            b != null && !string.IsNullOrEmpty(b.RelocateFromId) && b.BuildingId != null && b.BuildingId.EndsWith(UpgradeGhostSuffix, StringComparison.Ordinal);

        /// <summary>这座建筑能不能升级、升到什么（只看表与解锁，不看状态）。</summary>
        public static bool TryUpgradeTarget(CampaignState state, string typeId, out string toTypeId, out GridReason reason)
        {
            reason = GridReason.Of(GridBlockReason.NoUpgrade);
            if (!GridContent.TryGetUpgrade(typeId, out toTypeId) || !GridContent.TryGetBuilding(toTypeId, out BuildingGrid to))
            {
                toTypeId = null;
                return false;
            }
            if (!BuildCatalog.IsUnlocked(state, to.UnlockRule))
            {
                reason = new GridReason(GridBlockReason.UpgradeLocked, "plan.reason.upgrade_locked", DisplayName(toTypeId), to.UnlockHintKey);
                return false;
            }
            return true;
        }

        /// <summary>升级的差额（新造价 − 旧造价，不低于 0）与工期（新建筑的建造工期）。</summary>
        public static int UpgradeDiff(string fromTypeId, string toTypeId, out float seconds)
        {
            seconds = 1f;
            int from = HomeValleyLayout.BuildProfile.TryGetValue(fromTypeId, out (int ScrapCost, float Seconds) a) ? a.ScrapCost : 0;
            if (HomeValleyLayout.BuildProfile.TryGetValue(toTypeId, out (int ScrapCost, float Seconds) b))
            {
                seconds = Math.Max(0.1f, b.Seconds);
                return Math.Max(0, b.ScrapCost - from);
            }
            return 0;
        }

        /// <summary>
        /// FG4-ECO-05（FGR-ECO-012）：这座建筑升一级升到什么——有等级的建筑（fg.TbBuildingTier：仓库、信号塔）升到同类型的下一级（<paramref name="toTier"/> &gt; 0）；
        /// 其余按升级路线换成高一级的类型（<paramref name="toTier"/> = 0，FG3-LOG-07 的电塔 T1 → T2）。只看表与解锁，不看状态。
        /// </summary>
        public static bool TryUpgradeTarget(CampaignState state, BuildingRecord b, out string toTypeId, out int toTier, out GridReason reason)
        {
            toTier = 0;
            if (b != null && Economy.BuildingOps.HasTiers(b.BuildingTypeId))
            {
                toTypeId = null;
                reason = GridReason.Of(GridBlockReason.NoUpgrade);
                int cur = Economy.BuildingOps.TierOf(b);
                GameConfig.fg.BuildingTier next = Economy.BuildingOps.TierRow(b.BuildingTypeId, cur + 1);
                if (next == null)
                {
                    return false;
                }
                if (!BuildCatalog.IsUnlocked(state, next.UnlockRule))
                {
                    reason = new GridReason(GridBlockReason.UpgradeLocked, "plan.reason.upgrade_locked", Economy.BuildingOps.TierName(b.BuildingTypeId, next.Tier), next.UnlockHintKey);
                    return false;
                }
                toTypeId = b.BuildingTypeId;
                toTier = next.Tier;
                return true;
            }
            return TryUpgradeTarget(state, b?.BuildingTypeId, out toTypeId, out reason);
        }

        /// <summary>FG4-ECO-05：一座建筑升一级的差额与工期（有等级的建筑按 fg.TbBuildingTier；其余按新旧类型造价之差）。</summary>
        public static int UpgradeCost(BuildingRecord b, string toTypeId, int toTier, out float seconds)
        {
            if (toTier > 0 && b != null)
            {
                GameConfig.fg.BuildingTier row = Economy.BuildingOps.TierRow(b.BuildingTypeId, toTier);
                seconds = Math.Max(0.1f, row?.Seconds ?? 1f);
                return Math.Max(0, row?.DiffScrap ?? 0);
            }
            return UpgradeDiff(b?.BuildingTypeId, toTypeId, out seconds);
        }

        /// <summary>这座建筑现在能不能升级（状态检查）：已建成运转 / 玩家关停，没在搬迁 / 升级 / 拆除 / 维修；返回拒绝原因。</summary>
        public static bool CanUpgradeNow(CampaignState state, BuildingRecord b, out string toTypeId, out GridReason reason) =>
            CanUpgradeNow(state, b, out toTypeId, out _, out reason);

        /// <summary>同上，另给出目标等级（有等级的建筑 &gt; 0）。</summary>
        public static bool CanUpgradeNow(CampaignState state, BuildingRecord b, out string toTypeId, out int toTier, out GridReason reason)
        {
            toTypeId = null;
            toTier = 0;
            reason = GridReason.Of(GridBlockReason.NoBuilding);
            if (b == null || b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore || IsRelocationGhost(b))
            {
                reason = GridReason.Of(GridBlockReason.NoUpgrade);
                return false;
            }
            if (!TryUpgradeTarget(state, b, out toTypeId, out toTier, out reason))
            {
                return false;
            }
            if (FindRelocationGhost(state, b.BuildingId) is BuildingRecord ghost)
            {
                reason = GridReason.Of(IsUpgradeGhost(ghost) ? GridBlockReason.Upgrading : GridBlockReason.Relocating);
                return false;
            }
            if (b.ConstructionState == BuildingConstructionState.Damaged)
            {
                reason = GridReason.Of(GridBlockReason.RelocateDamaged);
                return false;
            }
            if ((b.ConstructionState != BuildingConstructionState.Operational && b.ConstructionState != BuildingConstructionState.Disabled)
                || HasActiveOrder(state, b.BuildingId, WorkOrderKind.Repair) || HasActiveOrder(state, b.BuildingId, WorkOrderKind.Salvage))
            {
                reason = GridReason.Of(GridBlockReason.Busy);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 原地升级一座建筑（升级规划的建筑部分、建筑面板的“升级”按钮）：生成升级目标虚影 + 施工单（差额取料）。返回 <see cref="GridOpResult.Kind.UpgradePlanned"/>（BuildingId = 虚影 ID）。
        /// 占地必须仍然合法（升级路线保证新旧占地相同；出口 / 障碍按新类型再核一次）。FG4-ECO-05：有等级的建筑虚影类型不变、带目标等级。
        /// </summary>
        public static GridOpResult TryUpgrade(CampaignState state, string buildingId)
        {
            BuildingRecord b = FindBuilding(state, buildingId);
            if (!CanUpgradeNow(state, b, out string toType, out int toTier, out GridReason why))
            {
                return GridOpResult.Fail(why);
            }
            GridPlacementResult check = ValidatePlacement(state, toType, new GridCell(b.GridX, b.GridY), GridMath.NormalizeRotation(b.Rotation),
                asPlayerPlacement: false, ignoreBuildingId: b.BuildingId, checkCost: false);
            if (!check.Ok)
            {
                return GridOpResult.Fail(check.Reasons[0], check);
            }
            int diff = UpgradeCost(b, toType, toTier, out float seconds);
            string ghostId = b.BuildingId + UpgradeGhostSuffix;
            HomeValleyWorkOrders.WorkOrderOpResult order = HomeValleyWorkOrders.TryCreateUpgradeAt(state, b, ghostId, toType, diff, seconds, toTier);
            if (!order.Success)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.Busy), check);
            }
            MapFor(state);
            return new GridOpResult(GridOpResult.Kind.UpgradePlanned, ghostId, check);
        }
    }
}
