using System;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG11-FAC-01（FG09 FGR-FAC-008 ①：每条专属行为都有 ≥ 0.8 秒的视觉 + 声音预警；B07 三通道）：超频专属语法的内核提示事件（<see cref="CombatEventKind.OverclockCue"/>）→ 反馈。
    /// 声音 + 字幕走 FeedbackCues（占位沿用系统音效，DEBT-FG11FAC01-01）；场景画面（散热鳍亮度 / 熔断半径圈与红光 / 三态颜色）目前没有订阅者，<see cref="Changed"/> 是留给 FG-M15 美术表现的挂点（DEBT-FG11FAC01-01）；熔断预警现在靠声音、字幕与镜头抖动。
    /// 引导钩子在这里发（解锁图鉴机制条目）。计数只给自检读。
    /// </summary>
    public static class OverclockCombatFeedback
    {
        /// <summary>某地点某个单位的超频表现要刷新（参数：地点、内核单位 ID、提示代码、超频状态）。</summary>
        public static event Action<CombatSite, int, byte, byte> Changed;

        public static int ShutdownCues { get; private set; }
        public static int VentCues { get; private set; }
        public static int MeltWarnCues { get; private set; }
        public static int BurstCues { get; private set; }
        public static int ModeWarnCues { get; private set; }
        public static int ModeCues { get; private set; }

        public static void ResetForTests()
        {
            ShutdownCues = VentCues = MeltWarnCues = BurstCues = ModeWarnCues = ModeCues = 0;
        }

        /// <summary>三态名称（字幕 / 悬停）。</summary>
        public static string ModeName(byte mode) => Localization.GameText.Get("overclock.mode." + Mathf.Clamp(mode, 0, 2));

        public static void OnCue(CombatSite site, in CombatEvent e, Vector2 at)
        {
            switch (e.Code)
            {
                case CombatOverclockConst.CueShutdown:
                    ShutdownCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.OverclockShutdown, at);
                    GuidanceHooks.Raise("overclock.first_shutdown");
                    break;
                case CombatOverclockConst.CueVent:
                    VentCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.OverclockVent, at);
                    GuidanceHooks.Raise("overclock.first_vent");
                    break;
                case CombatOverclockConst.CueMeltWarn:
                    MeltWarnCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.OverclockMeltWarn, at);
                    GuidanceHooks.Raise("overclock.first_melt");
                    break;
                case CombatOverclockConst.CueBurst:
                    BurstCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.OverclockBurst, at);
                    GuidanceHooks.Raise("overclock.first_burst");
                    break;
                case CombatOverclockConst.CueModeWarn:
                    ModeWarnCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.OverclockModeWarn, at);
                    break;
                case CombatOverclockConst.CueMode:
                    ModeCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.OverclockMode, at);
                    GuidanceHooks.Raise("overclock.first_mode");
                    break;
            }
            Changed?.Invoke(site, e.Unit, e.Code, e.Code2);
        }
    }
}
