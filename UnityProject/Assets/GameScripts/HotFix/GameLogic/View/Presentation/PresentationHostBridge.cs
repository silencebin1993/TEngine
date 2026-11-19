// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using System;
using System.Collections.Generic;
using BinGames.Presentation;
using GameLogic.Campaign;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.View.Presentation
{
    /// <summary>
    /// 热更层 → PL 表现宿主的适配（O(1)，零分配）：从 GameClock / WorldView / CampaignSession 拼 <see cref="PresentationFrame"/>。
    /// 正式入口：GameRoot.OnUpdate 在 WorldSimulation.Frame 之后调 <see cref="Tick"/>（SEAMS S-02）；采集 / 自检脚本在手动推进世界后调 <see cref="TickManual"/>。
    /// </summary>
    public static class PresentationHostBridge
    {
        /// <summary>
        /// 正式游戏里 PL 模块的唯一登记点：每张功能卡只在这里加一行 <c>new XxxModule(),</c>（顺序即 Tick 顺序），
        /// 不要在 GameRoot / CampaignSession 另开接缝。第一次 <see cref="Tick"/> 时逐个登记（自检的 TickManual 不登记，自检自己登记要测的模块）。
        /// </summary>
        private static readonly IPresentationModule[] GameModulesTable =
        {
        };

        private static bool _registered;

        public static IReadOnlyList<IPresentationModule> GameModules => GameModulesTable;

        public static void Tick(bool inWorld)
        {
            if (!_registered)
            {
                _registered = true;
                foreach (IPresentationModule m in GameModulesTable)
                {
                    try
                    {
                        PresentationHost.Register(m);
                    }
                    catch (Exception e)
                    {
                        // Id 没登记等编程错误：记一次，别让 GameRoot.OnUpdate 后面的 FG 逻辑断掉（PresentationValidate.Skeleton 会判 ✗）
                        Debug.LogException(e);
                    }
                }
            }
            // 总开关关且没有启用中的模块：连拼帧都不做
            if (!PresentationHost.NeedsTick)
            {
                return;
            }
            PresentationHost.Tick(BuildFrame(Time.unscaledDeltaTime, GameClock.FrameScaledDt, inWorld));
        }

        /// <summary>
        /// 手动推进世界（WorldSimulation.StepMany 等，不经 GameClock 帧推进）后调。<paramref name="gameDt"/> 为本次推进的游戏秒，
        /// 例如 StepMany(n) 后传 <c>GameClock.StepSeconds * n</c>；省略（&lt; 0）时取 GameClock.FrameScaledDt——它只在 GameClock 的帧推进里赋值，StepMany 之后恒为 0。
        /// </summary>
        public static void TickManual(float realDt, float gameDt = -1f)
        {
            // 先判 Current：没有战役时 WorldSimulation.AnyLoaded 会枚举空的 SortedDictionary（每次分配一个栈）
            PresentationHost.Tick(BuildFrame(realDt, gameDt < 0f ? GameClock.FrameScaledDt : gameDt,
                CampaignSession.Current != null && WorldSimulation.AnyLoaded));
        }

        public static PresentationFrame BuildFrame(float realDt, float gameDt, bool inWorld) =>
            new PresentationFrame(realDt, gameDt, GameClock.GameSeconds, GameClock.Ticks, GameClock.StepAlpha, GameClock.Paused,
                inWorld, WorldView.ObservedSiteId, WorldView.Camera);
    }
}
