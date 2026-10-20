using System;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG9-FAC-02（FG09 FGR-FAC-008 ①：每条专属行为都有 ≥ 0.8 秒的视觉 + 声音预警；B07 三通道）：铸造专属语法的内核提示事件（<see cref="CombatEventKind.FoundryCue"/>）→ 反馈。
    /// 声音 + 字幕走 FeedbackCues；画面（装填完成灯、雷区标记、冲锋蓄力、架设支腿）由地点表现订阅 <see cref="Changed"/> 事件驱动刷新，不每帧扫敌人。
    /// 计数只给自检读（内核里另有不丢的计数 CombatCounters.Salvos / AirIntercepts / MinesLaid / MinesDetonated / Charges）。
    /// </summary>
    public static class FoundryCombatFeedback
    {
        public const byte CodeSalvoWarn = 1;
        public const byte CodeIntercept = 2;
        public const byte CodeAirburst = 3;
        public const byte CodeMineArmed = 4;
        public const byte CodeMineBlast = 5;
        public const byte CodeChargeWindup = 6;
        public const byte CodeDeploy = 7;
        public const byte CodeMineLaid = 8;
        public const byte CodeRam = 9;

        /// <summary>某地点某个敌方单位的铸造表现要刷新（参数：地点、内核单位 ID、提示代码）。</summary>
        public static event Action<CombatSite, int, byte> Changed;

        public static int SalvoWarnCues { get; private set; }
        public static int InterceptCues { get; private set; }
        public static int MineArmedCues { get; private set; }
        public static int ChargeCues { get; private set; }
        public static int DeployCues { get; private set; }

        public static void ResetForTests()
        {
            SalvoWarnCues = InterceptCues = MineArmedCues = ChargeCues = DeployCues = 0;
        }

        public static void OnCue(CombatSite site, in CombatEvent e, Vector2 at)
        {
            switch (e.Code)
            {
                case CodeSalvoWarn:
                    SalvoWarnCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FoundrySalvoWarn, at);
                    GuidanceHooks.Raise("foundry.first_salvo");
                    break;
                case CodeIntercept:
                    InterceptCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FoundryIntercept, at);
                    break;
                case CodeAirburst:
                    FeedbackCues.RaiseAt(FeedbackCueId.FoundryAirburst, at);
                    break;
                case CodeMineArmed:
                    MineArmedCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FoundryMineArmed, at);
                    GuidanceHooks.Raise("foundry.first_mine");
                    break;
                case CodeMineBlast:
                    FeedbackCues.RaiseAt(FeedbackCueId.FoundryMineBlast, at);
                    break;
                case CodeChargeWindup:
                    ChargeCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FoundryCharge, at);
                    GuidanceHooks.Raise("foundry.first_charge");
                    break;
                case CodeDeploy:
                    DeployCues++;
                    FeedbackCues.RaiseAt(FeedbackCueId.FoundryDeploy, at);
                    GuidanceHooks.Raise("foundry.first_deploy");
                    break;
                case CodeRam:
                    FeedbackCues.RaiseAt(FeedbackCueId.ArmorHit, at);
                    break;
            }
            Changed?.Invoke(site, e.Unit, e.Code);
        }
    }
}
