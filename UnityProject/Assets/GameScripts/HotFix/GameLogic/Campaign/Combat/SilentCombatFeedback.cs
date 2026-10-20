using System;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG9-FAC-01（FG09 FGR-FAC-008 ①：每条专属行为都有 ≥ 0.8 秒的视觉 + 声音预警；B07 三通道）：静默专属语法的内核事件 → 反馈。
    /// - 提示事件（潜伏现形 / 偏折场阶段 / 反弹）→ 声音 + 字幕（FeedbackCues）+ 画面（地点表现订阅 <see cref="Changed"/>，事件驱动刷新，不每帧扫敌人）。
    /// - 玩法事件 LinkSevered（不丢）→ 被接入的那台强制断链、进入安全模式（原因“断链束”）。
    /// 计数只给自检读。
    /// </summary>
    public static class SilentCombatFeedback
    {
        /// <summary>某地点某个敌方单位的静默表现要刷新（潜伏 / 偏折场阶段变了）。参数：地点、内核单位 ID。</summary>
        public static event Action<CombatSite, int> Changed;

        public static int RevealCues { get; private set; }
        public static int StealthCues { get; private set; }
        public static int DeflectWarnCues { get; private set; }
        public static int ReflectCues { get; private set; }
        public static int SeverEvents { get; private set; }
        public static int SeverBreaks { get; private set; }

        public static void ResetForTests()
        {
            RevealCues = StealthCues = DeflectWarnCues = ReflectCues = SeverEvents = SeverBreaks = 0;
        }

        public static void OnCue(CombatSite site, in CombatEvent e, Vector2 at)
        {
            switch (e.Kind)
            {
                case CombatEventKind.StealthChanged:
                    if (e.Code == 1)
                    {
                        StealthCues++;
                        GuidanceHooks.Raise("silent.first_stealth");
                    }
                    else
                    {
                        RevealCues++;
                        FeedbackCues.RaiseAt(FeedbackCueId.SilentReveal, at);
                    }
                    Changed?.Invoke(site, e.Unit);
                    return;
                case CombatEventKind.DeflectPhase:
                    if (e.Code == 1)
                    {
                        DeflectWarnCues++;
                        FeedbackCues.RaiseAt(FeedbackCueId.SilentDeflect, at);
                        GuidanceHooks.Raise("silent.first_deflect");
                    }
                    Changed?.Invoke(site, e.Unit);
                    return;
                case CombatEventKind.Reflected:
                    ReflectCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.SilentReflect, at);
                    return;
            }
        }

        /// <summary>寂听者的断链束照射满时长：若这台仍是信号所在的机器，强制断链并进入安全模式（原因“断链束”）。</summary>
        public static void OnLinkSevered(CombatSite site, CampaignState state, int logicId, Vector2 at)
        {
            SeverEvents++;
            FeedbackCues.RaiseAt(FeedbackCueId.SilentSever, at);
            GuidanceHooks.Raise("silent.first_sever");
            if (state == null || site == null || SignalUplinkService.CurrentMachine(state) != logicId)
            {
                return;
            }
            RegionControlSystem control = RegionControlSystem.ForRegion(site.SiteId);
            if (control != null && control.ForceBreak(RegionControlChangeReason.SignalLost))
            {
                SeverBreaks++;
                SignalLinkService.EnterSafeModeFor(state, logicId, SignalLinkBreakReason.Severed);
            }
        }
    }
}
