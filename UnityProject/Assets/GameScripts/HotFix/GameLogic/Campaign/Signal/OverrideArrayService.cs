using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>超控阵列多出的槽为什么失效（玩家可见的原因，B05 / B06）。</summary>
    public enum OverrideOfflineReason : byte
    {
        None = 0,
        /// <summary>接入了电网，但电不够、被按优先级停机。</summary>
        NoPower = 1,
        /// <summary>不在任何电塔覆盖里。</summary>
        Unconnected = 2,
        /// <summary>玩家禁用了它。</summary>
        Disabled = 3,
        /// <summary>被摧毁（留着虚影，可以重建）。</summary>
        Destroyed = 4,
    }

    /// <summary>
    /// FG4-ECO-11（FG04 FGR-ECO-020；FG01 FGR-SIG-010；FGT-ECO-010）：超控阵列——信号核第 3～5 槽的唯一来源。
    ///
    /// - 等级：家园里已建成的超控阵列（运转 / 禁用 / 被摧毁，不含虚影与搬迁 / 升级目标）的最高等级 = “已解锁”的多出槽数（<see cref="BuiltTier"/>）；
    ///   运转中且电网结算为“有电”的最高等级 = “生效”的多出槽数（<see cref="ActiveTier"/>）。信号核只按生效的槽插固件（<see cref="SignalCoreService.ActiveContentIds"/>）。
    /// - 断电 / 未接入电网 / 禁用 / 被摧毁：多出的槽暂时失效——固件留在槽里、不生效，信号核面板与接入 HUD 写明原因与办法；来电后自动恢复，不用重新装（FGR-ECO-020）。
    /// - 变化检测复用电网结算的唯一出口（<see cref="HomeValleyPowerGrid"/> 的结果写回，<see cref="OnPowerApplied"/>）：完工、升级、拆除、启停、被摧毁、电力翻转
    ///   都会走一次电网结算，不另起轮询。只在等级真的变了时让信号核版本 +1（接入中的机器据此重编译）、发“失效 / 恢复”通知。
    /// 不依赖镜头：结算在模拟步里，家园没被观察时结果一样（FGR-BASE-021）。查询 O(超控阵列数)（家园最多一座），阵列列表按建筑数组引用缓存。
    /// </summary>
    public static class OverrideArrayService
    {
        public const string TypeId = "override_array";

        /// <summary>引导钩子：第一次建成超控阵列；第一次因为断电等原因失效（引导内容在 FG15-UX-04）。</summary>
        public const string HookFirstBuilt = GuidanceHooks.OverrideArrayFirstBuilt;
        public const string HookFirstOffline = GuidanceHooks.OverrideArrayFirstOffline;

        /// <summary>生效 / 已解锁等级变化（或读档同步）时 +1：信号核面板与 HUD 按它刷新。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>最近一次真正发出的失效 / 恢复通知条数（自检读）。</summary>
        public static int OfflineNotices { get; private set; }
        public static int OnlineNotices { get; private set; }

        private static BuildingRecord[] _indexed;
        private static readonly List<BuildingRecord> Arrays = new List<BuildingRecord>(1);

        /// <summary>家园里的超控阵列记录（含虚影；按建筑数组引用缓存，数组没换时 O(1)）。</summary>
        public static IReadOnlyList<BuildingRecord> ArraysOf(CampaignState s)
        {
            BuildingRecord[] records = s?.BuildingRecords;
            if (!ReferenceEquals(records, _indexed))
            {
                _indexed = records;
                Arrays.Clear();
                foreach (BuildingRecord b in records ?? Array.Empty<BuildingRecord>())
                {
                    if (b != null && b.BuildingTypeId == TypeId && b.RegionId == HomeValleyLayout.RegionId)
                    {
                        Arrays.Add(b);
                    }
                }
            }
            return Arrays;
        }

        /// <summary>已建成过的（运转 / 禁用 / 被摧毁），不是虚影、不是搬迁 / 升级目标。</summary>
        public static bool IsBuilt(BuildingRecord b) =>
            b != null && !HomeGridService.IsRelocationGhost(b)
            && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled
                || b.ConstructionState == BuildingConstructionState.Damaged);

        /// <summary>运转中且电网结算为“有电”。</summary>
        public static bool IsActive(BuildingRecord b) =>
            IsBuilt(b) && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered;

        /// <summary>等级最高的已建成阵列（没有返回 null）。</summary>
        public static BuildingRecord Primary(CampaignState s)
        {
            IReadOnlyList<BuildingRecord> list = ArraysOf(s);
            BuildingRecord best = null;
            for (int i = 0; i < list.Count; i++)
            {
                BuildingRecord b = list[i];
                if (IsBuilt(b) && (best == null || BuildingOps.TierOf(b) > BuildingOps.TierOf(best)))
                {
                    best = b;
                }
            }
            return best;
        }

        /// <summary>已解锁的多出槽数（已建成阵列的最高等级；没有 = 0）。</summary>
        public static int BuiltTier(CampaignState s)
        {
            IReadOnlyList<BuildingRecord> list = ArraysOf(s);
            int tier = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (IsBuilt(list[i]))
                {
                    tier = Math.Max(tier, BuildingOps.TierOf(list[i]));
                }
            }
            return tier;
        }

        /// <summary>生效的多出槽数（运转中、有电的阵列的最高等级；没有 = 0）。</summary>
        public static int ActiveTier(CampaignState s)
        {
            IReadOnlyList<BuildingRecord> list = ArraysOf(s);
            int tier = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (IsActive(list[i]))
                {
                    tier = Math.Max(tier, BuildingOps.TierOf(list[i]));
                }
            }
            return tier;
        }

        /// <summary>多出的槽为什么失效（看等级最高的那座）；都生效 / 没有阵列 = None。</summary>
        public static OverrideOfflineReason OfflineReason(CampaignState s)
        {
            BuildingRecord b = Primary(s);
            if (b == null || IsActive(b))
            {
                return OverrideOfflineReason.None;
            }
            switch (b.ConstructionState)
            {
                case BuildingConstructionState.Damaged:
                    return OverrideOfflineReason.Destroyed;
                case BuildingConstructionState.Disabled:
                    return OverrideOfflineReason.Disabled;
                default:
                    return b.PowerState == BuildingPowerState.Unpowered ? OverrideOfflineReason.Unconnected : OverrideOfflineReason.NoPower;
            }
        }

        public static string ReasonText(OverrideOfflineReason r) => r switch
        {
            OverrideOfflineReason.Unconnected => GameText.Get("override.reason.unconnected"),
            OverrideOfflineReason.Disabled => GameText.Get("override.reason.disabled"),
            OverrideOfflineReason.Destroyed => GameText.Get("override.reason.destroyed"),
            _ => GameText.Get("override.reason.no_power"),
        };

        public static string FixText(OverrideOfflineReason r) => r switch
        {
            OverrideOfflineReason.Unconnected => GameText.Get("override.reason.fix.unconnected"),
            OverrideOfflineReason.Disabled => GameText.Get("override.reason.fix.disabled"),
            OverrideOfflineReason.Destroyed => GameText.Get("override.reason.fix.destroyed"),
            _ => GameText.Get("override.reason.fix.no_power"),
        };

        /// <summary>“第 3 槽” / “第 3～4 槽”（1 起的槽号，闭区间）。</summary>
        public static string SlotsText(int fromSlot, int toSlot) =>
            fromSlot >= toSlot
                ? GameText.Format("override.slots_one", fromSlot.ToString(CultureInfo.InvariantCulture))
                : GameText.Format("override.slots_range", fromSlot.ToString(CultureInfo.InvariantCulture), toSlot.ToString(CultureInfo.InvariantCulture));

        /// <summary>面板与 HUD 的变化键（等级、生效等级、原因；O(1)）。</summary>
        public static int StateKey(CampaignState s) => HashCode.Combine(BuiltTier(s), ActiveTier(s), (int)OfflineReason(s), Revision);

        /// <summary>
        /// 解锁第 <paramref name="tier"/> 级要什么（锁定槽位的说明，卡片“关键材料缺失时，面板说明从哪里获得”）：
        /// “建造超控阵列：关键材料 监听阵列核 ×1（核心保管库 0）——缺 监听阵列核 ×1：主线：击败……”；研究节点写在前面。
        /// </summary>
        public static string RequirementText(CampaignState s, int tier)
        {
            tier = Math.Max(1, tier);
            string what = tier == 1 ? GameText.Get("override.requirement.build") : GameText.Format("override.requirement.upgrade", tier);
            BuildingTier row = BuildingOps.TierRow(TypeId, tier);
            string research = row != null ? ResearchGate.Describe(s, row.UnlockRule) : string.Empty;
            IReadOnlyList<BuildMaterialNeed> need = BuildMaterials.For(TypeId, tier);
            string materials = BuildMaterials.DescribeShortfall(s, need) ?? GameText.Get("override.requirement.have");
            string body = research.Length > 0 ? GameText.Format("override.requirement.join", research, materials) : materials;
            return GameText.Format("override.requirement", what, body);
        }

        // ── 电网结算后的变化检测（唯一写入口：SignalCoreState.OverrideBuiltTierSeen / OverrideActiveTierSeen）──────────────

        /// <summary>
        /// 电网每次把结算结果写回建筑之后调用（<see cref="HomeValleyPowerGrid"/>，O(阵列数)）。等级没变什么都不做；变了：信号核版本 +1（接入中的机器重编译、
        /// 面板与 HUD 刷新），并在 <paramref name="notify"/> 时发“失效 / 恢复”通知（可定位、同类聚合、进历史与离家报告的电力段）与引导钩子。
        /// 读档、换战役后的第一次结算 <paramref name="notify"/> = false：只同步“上次看到的等级”，不补发提醒。
        /// </summary>
        public static void OnPowerApplied(CampaignState s, bool notify)
        {
            if (s == null)
            {
                return;
            }
            if (s.SignalCore == null)
            {
                CampaignFgStateDomains.EnsureAll(s); // 只在极旧的存档里缺域时补（电网每次结算都会走到这里，不每次跑整套补齐）
            }
            SignalCoreState core = s.SignalCore;
            int built = BuiltTier(s);
            int active = Math.Min(built, ActiveTier(s));
            int oldBuilt = core.OverrideBuiltTierSeen;
            int oldActive = core.OverrideActiveTierSeen;
            if (built == oldBuilt && active == oldActive)
            {
                return;
            }
            core.OverrideBuiltTierSeen = built;
            core.OverrideActiveTierSeen = active;
            Revision++;
            SignalCoreService.NotifyOverrideChanged();
            if (!notify)
            {
                return;
            }
            int initial = SignalCoreService.InitialSlots;
            BuildingRecord primary = Primary(s);
            Vector3? at = primary != null ? new Vector3(primary.Position.x, 0f, primary.Position.y) : (Vector3?)null;
            if (built > oldBuilt && oldBuilt == 0)
            {
                GuidanceHooks.Raise(HookFirstBuilt);
            }
            if (active < oldActive)
            {
                // 失效：生效的槽少了（断电 / 未接入 / 禁用 / 被摧毁 / 拆除）。拆除时没有阵列可定位，写“已拆除”。
                OverrideOfflineReason reason = OfflineReason(s);
                string name = primary != null ? BuildingOps.NameOf(primary) : HomeGridService.DisplayName(TypeId);
                string slots = SlotsText(initial + active + 1, initial + oldActive);
                string fix = primary != null ? FixText(reason) : GameText.Get("override.reason.fix.removed");
                string why = primary != null ? ReasonText(reason) : GameText.Get("override.reason.removed");
                NotificationCenter.Post("override_offline", GameText.Format("override.notify.offline", name, why, slots, fix), at);
                OfflineNotices++;
                GuidanceHooks.Raise(HookFirstOffline);
            }
            else if (active > oldActive && built == oldBuilt)
            {
                // 恢复：等级没变、生效的槽回来了（来电 / 接回电网 / 启用 / 重建完工）。新建与升级完工有自己的“已建成”反馈，不重复发。
                NotificationCenter.Post("override_online", GameText.Format("override.notify.online", SlotsText(initial + oldActive + 1, initial + active)), at);
                OnlineNotices++;
            }
        }

        /// <summary>自检用：清掉缓存与计数。</summary>
        public static void ResetForTests()
        {
            _indexed = null;
            Arrays.Clear();
            OfflineNotices = 0;
            OnlineNotices = 0;
            Revision++;
        }
    }
}
