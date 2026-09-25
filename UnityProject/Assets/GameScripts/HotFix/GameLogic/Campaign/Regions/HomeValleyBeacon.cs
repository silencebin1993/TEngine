using System.Linq;
using TEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER7-BEACON-01 STORY-EXECUTION-CARDS.md：导航信标建造/供电/启动的唯一判定入口。建造
    /// 本身复用既有 `HomeValleyWorkOrders.TryCreateBuild`/`HomeValleyPowerGrid`（不新造第二套建造/
    /// 供电系统，只新增 <see cref="HomeValleyLayout.BuildingTypeBeacon"/> 这一条内容），本类只负责
    /// "解锁判定"+"启动确认（二次确认+10秒不可取消演出）"这一段真正缺失的逻辑。
    ///
    /// ── 二次确认 + 不可取消演出 ──
    /// <see cref="TryStartLaunch"/> 是第二次确认（第一次是玩家在 UI 面板点"确认启动"按钮本身，见
    /// <c>BeaconLaunchPanelUIToolkit</c>），只在 Operational+Powered+尚未启动过时生效，成功后写
    /// <see cref="CampaignState.BeaconLaunchStartedAtPlaySeconds"/>——从这一刻起 <see cref="IsLaunching"/>
    /// 恒真直到 10 秒后 <see cref="Tick"/> 授予终态事件，UI 层没有任何"取消"入口可以清空这个时间戳
    /// （"不可取消"落在"没有提供撤销 API"这个事实本身，不是靠 UI 按钮灰掉这种更弱的保证）。</summary>
    public static class HomeValleyBeacon
    {
        /// <summary>DEMO-CONTENT-LOCK.md 未点名具体秒数，卡片原文"进入不可取消10秒演出"——逐字10秒，
        /// 不是本 Story 的 judgment call。</summary>
        public const float LaunchCutsceneSeconds = 10f;

        public readonly struct ActionResult
        {
            public readonly bool Success;
            public readonly string FailureReason;

            private ActionResult(bool success, string failureReason)
            {
                Success = success;
                FailureReason = failureReason;
            }

            public static ActionResult Ok() => new ActionResult(true, null);
            public static ActionResult Fail(string reason) => new ActionResult(false, reason);
        }

        /// <summary>解锁——OBJ-09 完成才允许建造位可见/可点选建造，唯一判据（不重复判定其四个子
        /// 条件，"核心数据缺失"等负向路径天然由这条门禁统一拦截，见 <see cref="TryStartLaunch"/>
        /// 类注释）。</summary>
        public static bool IsUnlocked(CampaignState state) =>
            CampaignObjectiveTracker.IsCompleted(state, CampaignObjectiveTracker.Obj09);

        private static BuildingRecord FindBuilding(CampaignState state) =>
            state?.BuildingRecords?.FirstOrDefault(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeBeacon);

        public static bool Exists(CampaignState state) => FindBuilding(state) != null;

        public static bool IsOperationalAndPowered(CampaignState state)
        {
            BuildingRecord b = FindBuilding(state);
            return b != null && b.ConstructionState == BuildingConstructionState.Operational
                && b.PowerState == BuildingPowerState.Powered;
        }

        public static bool IsLaunched(CampaignState state) =>
            state?.EventLedger != null && state.EventLedger.Any(e => e.EventId == CampaignObjectiveTracker.BeaconLaunchEventId);

        public static bool IsLaunching(CampaignState state) =>
            state != null && state.BeaconLaunchStartedAtPlaySeconds >= 0f && !IsLaunched(state);

        /// <summary>剩余演出秒数，供 UI 倒计时展示；未在演出中时返回0。</summary>
        public static float RemainingCutsceneSeconds(CampaignState state)
        {
            if (!IsLaunching(state))
            {
                return 0f;
            }
            float remaining = state.BeaconLaunchStartedAtPlaySeconds + LaunchCutsceneSeconds - state.PlaySeconds;
            return remaining > 0f ? remaining : 0f;
        }

        /// <summary>二次确认后的真正启动——"断电/核心数据缺失/重复按E不启动，不扣二次款"三条负向路径
        /// 分别对应：断电→<see cref="IsOperationalAndPowered"/> 为假；核心数据缺失→结构上蕴含在
        /// <see cref="IsUnlocked"/>（OBJ-09 未完成就不可能解锁，不需要重复查一遍
        /// <see cref="FoundryOutpostLayout.CoreDataContentId"/>）；重复按→<see cref="IsLaunched"/>/
        /// <see cref="IsLaunching"/> 双重幂等拦截。本方法不涉及任何经济事务（120废料已在建造时通过
        /// <see cref="HomeValleyWorkOrders.TryCreateBuild"/> 的既有 Propose/Reserve/Commit 流程扣过，
        /// 启动本身零成本），"不扣二次款"因此天然成立，不需要额外代码。</summary>
        public static ActionResult TryStartLaunch(CampaignState state)
        {
            if (state == null)
            {
                return ActionResult.Fail("no-active-campaign");
            }
            if (IsLaunched(state))
            {
                return ActionResult.Fail("already-launched");
            }
            if (IsLaunching(state))
            {
                return ActionResult.Fail("already-launching");
            }
            if (!IsUnlocked(state))
            {
                return ActionResult.Fail("not-unlocked");
            }
            if (!IsOperationalAndPowered(state))
            {
                return ActionResult.Fail("not-operational-or-powered");
            }

            state.BeaconLaunchStartedAtPlaySeconds = state.PlaySeconds;
            Log.Info("[HomeValleyBeacon] 信标启动确认：进入10秒不可取消演出。");
            // ER8-CONTENT-01 AC-AUD-001 信标：音色取 BuildingCatalog 信标的 SfxId。
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.BeaconLaunch, "10 秒后发射，无法取消", Feedback.FeedbackCues.BuildingTypeSfx(HomeValleyLayout.BuildingTypeBeacon));
            return ActionResult.Ok();
        }

        /// <summary>每帧驱动——演出计时到期后授予一次性事件并触发 OBJ-10 重算。唯一调用点
        /// <see cref="HomeValleyController.Update"/>。</summary>
        public static void Tick(CampaignState state, float dt)
        {
            if (state == null || dt <= 0f || !IsLaunching(state))
            {
                return;
            }
            if (state.PlaySeconds < state.BeaconLaunchStartedAtPlaySeconds + LaunchCutsceneSeconds)
            {
                return;
            }
            CampaignEventLedger.TryGrant(state, CampaignObjectiveTracker.BeaconLaunchEventId, "BeaconLaunch", state.PlaySeconds, null);
            CampaignObjectiveTracker.Recompute(state);
            Log.Info("[HomeValleyBeacon] 演出结束：信标启动事件已授予，campaignPhase→Completed。");
            // 事件授予后 IsLaunching 翻 false，本分支只会执行一次。
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Victory);
        }
    }
}
