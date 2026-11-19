using System;
using System.Globalization;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG13-FAC-01（FG09 FGR-FAC-008 ①：每条专属行为都有 ≥ 0.8 秒的视觉 + 声音预警；B07 三通道）：白潮专属语法的内核提示事件（<see cref="CombatEventKind.FleetCue"/>）→ 反馈。
    /// 声音 + 字幕走 FeedbackCues（占位沿用系统音效，DEBT-FG13FAC01-01）；地图上的打击圈 / 屏蔽范围 / 焦土由 WorldMapShared 按内核状态画（不靠本事件）；
    /// <see cref="Changed"/> 是留给 FG-M15 美术表现的挂点。引导钩子在这里发（解锁图鉴机制条目）。计数只给自检读。
    /// </summary>
    public static class FleetCombatFeedback
    {
        /// <summary>某地点白潮表现要刷新（参数：地点、内核单位 ID（打击类为 0）、提示代码、Other）。</summary>
        public static event Action<CombatSite, int, byte, int> Changed;

        public static int BeamAimCues { get; private set; }
        public static int BeamCues { get; private set; }
        public static int RecoverCues { get; private set; }
        public static int StrikeMarkCues { get; private set; }
        public static int StrikeCancelCues { get; private set; }
        public static int StrikeCues { get; private set; }
        public static int PodWarnCues { get; private set; }
        public static int PodLandCues { get; private set; }
        public static int FormationBrokenCues { get; private set; }
        public static int ShieldBreakCues { get; private set; }
        public static int GateHitCues { get; private set; }
        public static int GateBreakCues { get; private set; }

        public static void ResetForTests()
        {
            BeamAimCues = BeamCues = RecoverCues = StrikeMarkCues = StrikeCancelCues = StrikeCues = PodWarnCues = PodLandCues = FormationBrokenCues = ShieldBreakCues = 0;
            GateHitCues = GateBreakCues = 0;
        }

        private static string Sec(float v) => Mathf.Max(0f, v).ToString("0.#", CultureInfo.InvariantCulture);

        public static void OnCue(CombatSite site, in CombatEvent e, Vector2 at)
        {
            switch (e.Code)
            {
                case CombatFleetConst.CueBeamAim:
                    BeamAimCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetBeamAim, at);
                    break;
                case CombatFleetConst.CueBeam:
                    BeamCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetBeam, at);
                    GuidanceHooks.Raise("fleet.first_inert");
                    break;
                case CombatFleetConst.CueInertRecover:
                    RecoverCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetInertRecover, at);
                    break;
                case CombatFleetConst.CueStrikeMark:
                    StrikeMarkCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetStrikeMark, at, Sec(e.Value));
                    GuidanceHooks.Raise("fleet.first_strike_mark");
                    break;
                case CombatFleetConst.CueStrikeCancel:
                    StrikeCancelCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetStrikeCancel, at);
                    break;
                case CombatFleetConst.CueStrike:
                    StrikeCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetStrike, at);
                    break;
                case CombatFleetConst.CuePodWarn:
                    PodWarnCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetPodWarn, at, Sec(e.Value));
                    GuidanceHooks.Raise("fleet.first_pod");
                    break;
                case CombatFleetConst.CuePodLand:
                    PodLandCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetPodLand, at);
                    break;
                case CombatFleetConst.CueFormationBroken:
                    FormationBrokenCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetFormationBroken, at);
                    GuidanceHooks.Raise("fleet.first_formation_broken");
                    break;
                case CombatFleetConst.CueShieldBreak:
                    ShieldBreakCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetShieldBreak, at);
                    GuidanceHooks.Raise("fleet.first_shield_break");
                    break;
                case CombatFleetConst.CueGateHit:
                    GateHitCues++; // FG13-END-05：护盾节点被它怕的端口削减（每 10% 一次）
                    FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, at);
                    break;
                case CombatFleetConst.CueGateBreak:
                    GateBreakCues++; // FG13-END-05：护盾节点被破
                    FeedbackCues.RaiseAt(FeedbackCueId.FleetShieldBreak, at);
                    break;
            }
            Changed?.Invoke(site, e.Unit, e.Code, e.Other);
        }
    }
}
