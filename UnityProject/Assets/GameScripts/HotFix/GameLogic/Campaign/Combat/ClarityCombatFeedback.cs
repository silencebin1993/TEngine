using System;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG10-FAC-01（FG09 FGR-FAC-008 ①：每条专属行为都有 ≥ 0.8 秒的视觉 + 声音预警；B07 三通道）：澄净专属语法的内核提示事件（<see cref="CombatEventKind.ClarityCue"/>）→ 反馈。
    /// 声音 + 字幕走 FeedbackCues；画面（喷淋锥形落点、枢轴扫臂与预警扇区、自爆圈、投放落点、喷淋池 / 油迹 / 分裂液）由内核渲染缓冲直接画（琥珀色预警圈 / 斑点，
    /// 内核写，表现只读），地点表现另可订阅 <see cref="Changed"/>。引导钩子在这里发（解锁图鉴条目）。
    /// 计数只给自检读（内核里另有不丢的计数 CombatCounters.Sprays / Freezes / Bursts / Deflagrations / SlicksIgnited / Splits / Repollutions / AcidTicks / PoolsWashed / PivotHits）。
    /// </summary>
    public static class ClarityCombatFeedback
    {
        public const byte CodeSprayWarn = 1;
        public const byte CodeSpray = 2;
        public const byte CodePivotWarn = 3;
        public const byte CodeBurstWarn = 4;
        public const byte CodeBurst = 5;
        public const byte CodeSlickIgnite = 6;
        public const byte CodeFreeze = 7;
        public const byte CodeSplit = 8;
        public const byte CodeDroneWarn = 9;
        public const byte CodePoolWashed = 10;
        public const byte CodeAcid = 11;

        /// <summary>某地点某个单位的澄净表现要刷新（参数：地点、内核单位 ID、提示代码）。</summary>
        public static event Action<CombatSite, int, byte> Changed;

        /// <summary>灭活无人机投放完成（地点、目标点、目标点半径）。FG10-RAID-01 订阅：目标点（净化塔）周围再次污染、写离家报告。</summary>
        public static event Action<CombatSite, Vector2, float> RepollutedAt;

        public static int SprayWarnCues { get; private set; }
        public static int PivotWarnCues { get; private set; }
        public static int BurstWarnCues { get; private set; }
        public static int DeflagrateCues { get; private set; }
        public static int SlickIgniteCues { get; private set; }
        public static int FreezeCues { get; private set; }
        public static int SplitCues { get; private set; }
        public static int DroneWarnCues { get; private set; }
        public static int PoolWashedCues { get; private set; }
        public static int AcidCues { get; private set; }
        public static int RepollutedEvents { get; private set; }

        public static void ResetForTests()
        {
            SprayWarnCues = PivotWarnCues = BurstWarnCues = DeflagrateCues = SlickIgniteCues = FreezeCues = 0;
            SplitCues = DroneWarnCues = PoolWashedCues = AcidCues = RepollutedEvents = 0;
        }

        public static void OnCue(CombatSite site, in CombatEvent e, Vector2 at)
        {
            bool cryo = e.Code2 == 2;
            switch (e.Code)
            {
                case CodeSprayWarn:
                    SprayWarnCues++;
                    FeedbackCues.RaiseAt(cryo ? FeedbackCueId.ClaritySprayWarnCryo : FeedbackCueId.ClaritySprayWarn, at);
                    break;
                case CodeSpray:
                    FeedbackCues.RaiseAt(cryo ? FeedbackCueId.ClaritySprayCryo : FeedbackCueId.ClaritySpray, at);
                    GuidanceHooks.Raise("clarity.first_spray");
                    break;
                case CodePivotWarn:
                    PivotWarnCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.ClarityPivotWarn, at);
                    GuidanceHooks.Raise("clarity.first_pivot");
                    break;
                case CodeBurstWarn:
                    BurstWarnCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.ClarityBurstWarn, at);
                    break;
                case CodeBurst:
                    if (e.Code2 == 1)
                    {
                        DeflagrateCues++;
                        FeedbackCues.RaiseAt(FeedbackCueId.ClarityDeflagrate, at);
                    }
                    else
                    {
                        FeedbackCues.RaiseAt(FeedbackCueId.ClarityBurst, at);
                    }
                    GuidanceHooks.Raise("clarity.first_burst");
                    break;
                case CodeSlickIgnite:
                    SlickIgniteCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.ClaritySlickIgnite, at);
                    GuidanceHooks.Raise("clarity.first_slick_ignite");
                    break;
                case CodeFreeze:
                    FreezeCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.ClarityFreeze, at);
                    GuidanceHooks.Raise("clarity.first_freeze");
                    break;
                case CodeSplit:
                    SplitCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.ClaritySplit, at);
                    GuidanceHooks.Raise("clarity.first_split");
                    break;
                case CodeDroneWarn:
                    DroneWarnCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.ClarityDroneWarn, at);
                    GuidanceHooks.Raise("clarity.first_drone");
                    break;
                case CodePoolWashed:
                    PoolWashedCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.ClarityPoolWashed, at);
                    GuidanceHooks.Raise("clarity.first_pool_washed");
                    break;
                case CodeAcid:
                    AcidCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.ClarityAcid, at);
                    GuidanceHooks.Raise("clarity.first_acid");
                    break;
            }
            Changed?.Invoke(site, e.Unit, e.Code);
        }

        /// <summary>投放完成（不丢的玩法事件）：记一次并转给订阅者（FG10-RAID-01 的再污染）；没有订阅者时写日志（投放目标由 RAID-01 写入，本卡不写，不会发生）。</summary>
        public static void OnRepolluted(CombatSite site, in CombatEvent e, Vector2 at)
        {
            RepollutedEvents++;
            if (RepollutedAt == null)
            {
                Log.Warning($"[ClarityCombatFeedback] 灭活无人机在 ({at.x:F1},{at.y:F1}) 投放完成，但没有再污染订阅者（FG10-RAID-01 接入前不写投放目标）。");
                return;
            }
            RepollutedAt.Invoke(site, at, e.Value);
        }
    }
}
