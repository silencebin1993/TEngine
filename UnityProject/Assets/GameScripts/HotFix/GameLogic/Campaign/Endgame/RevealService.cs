using System;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Endgame
{
    /// <summary>
    /// FG13-END-02 揭示（FG11 FGR-END-010；第 9 节“揭示演出中退出游戏 → 读档后回到演出前的自动存档”；验收 FGT-END-003）。
    ///
    /// - 触发（FGR-END-010“玩家在信标点火后确认启动，沿用现有的二次确认面板”）：五个巨构阶段全部完工（<see cref="MegastructureService.AllCompletedEvent"/>，信标点火）后，
    ///   发一条“信标待点亮”通知；玩家选中返航工地点“点亮信标…”（第一次）→ 信标确认面板（Demo 的信标二次确认面板，同一份 UXML）点“确认点亮”（第二次）→ <see cref="Confirm"/>。
    ///   没有时间压力（FG11 第 8 节），只有主线任务“返航工程”提示。
    /// - 演出：<see cref="Confirm"/> 投放资料库条目 node.reveal（演出，镜头 sky）→ 叙事框架记成待播 → 播放器开始播放前自动存档（<see cref="SaveReason.CutsceneEnter"/>，存档里记着待播：
    ///   演出中退出游戏，读档后回到这一份、重新播放）→ 演出可跳过（首次要确认）、之后在资料库重看（不再改状态、不再存档）。
    /// - 状态变化（揭示收进资料库那一步，看完或跳过同一步）：事件账本 <see cref="RevealedEventId"/>（唯一真相，随存档）→ HUD“轨道威胁”指示、主线任务更新（“返航工程”完成、“守住家园”出现）、
    ///   音乐切到轨道威胁层（<see cref="Feedback.RaidAudioDirector"/>）、“轨道威胁”通知、<see cref="RevealedEvent"/>；然后再自动存档一次（<see cref="SaveReason.RevealComplete"/>）。
    /// 每次调用 O(1)（事件账本查找按条数，几十条）。
    /// </summary>
    public static class RevealService
    {
        /// <summary>资料库条目（fg.TbNarrativeEntry）与触发。</summary>
        public const string EntryId = "node.reveal";
        public const string Trigger = "endgame:reveal";
        /// <summary>事件账本：揭示已发生（轨道威胁激活；主线任务 main.homecoming 的完成条件）。</summary>
        public const string RevealedEventId = "endgame_revealed";
        /// <summary>GameEvent：揭示的状态变化刚落定（END-03 清洗波次等订阅）。</summary>
        public const string RevealedEvent = "Endgame.Revealed";
        public const string BeaconReadyNotifyType = "mega_beacon_ready";
        public const string OrbitalNotifyType = "orbital_threat";

        public const string DenyNotDone = "not_done";
        public const string DenyDone = "done";
        public const string DenyPending = "pending";
        public const string DenyBusy = "busy";
        public const string DenyNoSite = "no_site";

        /// <summary>演出后的自动存档（默认 = 正式自动存档入口；自检可替换）。</summary>
        public static Func<SaveResult> AfterSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.RevealComplete, toast: false);

        private static bool _listening;

        /// <summary>状态版本（HUD 刷新键）。</summary>
        public static int Revision { get; private set; }
        /// <summary>自检读：本进程确认 / 状态落定 / 演出后存档的次数，最后一次存档结果。</summary>
        public static int ConfirmCount { get; private set; }
        public static int RevealCount { get; private set; }
        public static int AfterSaveCount { get; private set; }
        public static SaveResult LastAfterSave { get; private set; }
        public static int BeaconReadyCount { get; private set; }

        public static void EnsureListening()
        {
            if (_listening)
            {
                return;
            }
            _listening = true;
            GameEvent.AddEventListener<string>(NarrativeService.AddedEvent, OnNarrativeAdded);
            GameEvent.AddEventListener(MegastructureService.AllCompletedEvent, OnAllCompleted);
        }

        public static void StopListeningForTests()
        {
            if (!_listening)
            {
                return;
            }
            _listening = false;
            GameEvent.RemoveEventListener<string>(NarrativeService.AddedEvent, OnNarrativeAdded);
            GameEvent.RemoveEventListener(MegastructureService.AllCompletedEvent, OnAllCompleted);
        }

        // ── 读 ───────────────────────────────────────────────────────────

        /// <summary>揭示已发生（轨道威胁激活）。</summary>
        public static bool Revealed(CampaignState s) => s != null && CampaignEventLedger.Contains(s, RevealedEventId);

        /// <summary>HUD“轨道威胁”指示 / 音乐层的判据（揭示之后一直为真；END-03 起清洗波次按它开始）。</summary>
        public static bool OrbitalThreatActive(CampaignState s) => Revealed(s);

        /// <summary>已确认、演出还没播完（存档里记着待播）。</summary>
        public static bool Pending(CampaignState s) => s?.Progress != null && s.Progress.PendingCutscene == EntryId;

        /// <summary>信标已点火、等玩家确认点亮（工地面板显示“点亮信标…”）。</summary>
        public static bool AwaitingConfirm(CampaignState s) => MegastructureService.AllDone(s) && !Revealed(s) && !Pending(s) && !NarrativeService.Has(s, EntryId);

        /// <summary>返航工地（没有 / 已被摧毁 = null）。</summary>
        public static BuildingRecord Site(CampaignState s)
        {
            if (s?.BuildingRecords == null)
            {
                return null;
            }
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && b.BuildingTypeId == MegastructureService.TypeId && b.ConstructionState != BuildingConstructionState.Destroyed)
                {
                    return b;
                }
            }
            return null;
        }

        /// <summary>现在能不能确认点亮；不能时 <paramref name="reason"/> = Deny*。</summary>
        public static bool CanConfirm(CampaignState s, out string reason)
        {
            reason = null;
            if (Revealed(s) || NarrativeService.Has(s, EntryId))
            {
                reason = DenyDone;
            }
            else if (Pending(s))
            {
                reason = DenyPending;
            }
            else if (!MegastructureService.AllDone(s))
            {
                reason = DenyNotDone;
            }
            else if (Site(s) == null)
            {
                reason = DenyNoSite;
            }
            else if (NarrativeService.Playing != null || !string.IsNullOrEmpty(s.Progress?.PendingCutscene))
            {
                reason = DenyBusy;
            }
            return reason == null;
        }

        public static string DenyText(string reason) => string.IsNullOrEmpty(reason) ? string.Empty : GameText.Get("mega.reveal.deny." + reason);

        // ── 写 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 确认点亮信标（信标确认面板的“确认点亮”）：投放揭示演出（记成待播；播放器下一帧开始播放，开始前自动存档）。
        /// 不满足条件时什么也不改、返回 false 与原因（<see cref="DenyText"/>）。
        /// </summary>
        public static bool Confirm(CampaignState s, out string reason)
        {
            if (!CanConfirm(s, out reason))
            {
                return false;
            }
            EnsureListening();
            if (!NarrativeService.Trigger(s, Trigger))
            {
                reason = DenyDone; // 表里没有揭示条目 / 已收过（表坏了的兜底；生成前校验保证有）
                return false;
            }
            ConfirmCount++;
            Revision++;
            Log.Info("[RevealService] 玩家确认点亮信标：揭示演出待播");
            return true;
        }

        private static void OnAllCompleted() => PostBeaconReady(CampaignSession.Current);

        private static int _readyPostedFor; // 只记身份哈希，不持有旧战役引用（ERD-PER-002）

        /// <summary>审查 P2：进入战役（读档 / 回滚）时信标仍待点亮 → 补发一次“信标待点亮”（同一份战役状态只补一次）。</summary>
        public static void OnCampaignEntered(CampaignState s)
        {
            if (s != null && System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s) != _readyPostedFor)
            {
                PostBeaconReady(s);
            }
        }

        private static void PostBeaconReady(CampaignState s)
        {
            if (s == null || !AwaitingConfirm(s))
            {
                return;
            }
            _readyPostedFor = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s);
            BeaconReadyCount++;
            Revision++;
            BuildingRecord site = Site(s);
            NotificationCenter.Post(BeaconReadyNotifyType, GameText.Format("mega.notify.beacon_ready",
                    site != null ? Feedback.FeedbackCues.BuildingLabel(site.BuildingId) : GameText.Get("building.megastructure_site.name")),
                site != null ? new Vector3(site.Position.x, 0f, site.Position.y) : (Vector3?)null);
        }

        private static void OnNarrativeAdded(string id)
        {
            if (id != EntryId)
            {
                return;
            }
            Apply(CampaignSession.Current);
        }

        /// <summary>揭示的状态变化（幂等：已揭示过的不再改、不再存档）。</summary>
        private static void Apply(CampaignState s)
        {
            if (s == null || !CampaignEventLedger.TryGrant(s, RevealedEventId, "Endgame", s.PlaySeconds, EntryId))
            {
                return;
            }
            RevealCount++;
            Revision++;
            Log.Info("[RevealService] 揭示：净化协议启动，轨道威胁激活");
            CampaignObjectiveTracker.Recompute(s); // 任务更新：返航工程完成 → 守住家园出现
            NotificationCenter.Post(OrbitalNotifyType, GameText.Get("mega.notify.orbital_threat"));
            GameEvent.Send(RevealedEvent);
            LastAfterSave = AfterSaveHandler != null ? AfterSaveHandler() : default;
            AfterSaveCount++;
        }
    }
}
