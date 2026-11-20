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
            new VehicleRigModule(), // PL-VEH-02
        };

        private static bool _registered;

        // PL-INF-07 重建检测（只读、O(1)、零分配）：上一帧看到的战役引用 / 观察地点 / 切换计数 / 是否在世界里
        private static CampaignState _seenSession;
        private static string _seenSiteId;
        private static int _seenSwitchCount;
        private static bool _seenInWorld;

        public static IReadOnlyList<IPresentationModule> GameModules => GameModulesTable;

        public static void Tick(bool inWorld)
        {
            if (!_registered)
            {
                _registered = true;
                VehicleRigAdapter.Install(); // PL-VEH-02：接 PresentationHooks 的机器表现挂上 / 摘下（只存引用；功能关时不写变换）
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
            PresentationHost.Tick(LiveFrame(Time.unscaledDeltaTime, inWorld));
        }

        /// <summary>PL-INF-07 Live 时钟：GameDt = GameClock.FrameScaledDt（暂停 0、含倍速与慢放），RealDt 由调用方给（正式路径 Time.unscaledDeltaTime）。会先做重建检测。</summary>
        public static PresentationFrame LiveFrame(float realDt, bool inWorld) => BuildFrame(realDt, GameClock.FrameScaledDt, inWorld);

        /// <summary>
        /// 手动推进世界（WorldSimulation.StepMany 等，不经 GameClock 帧推进）后调。<paramref name="gameDt"/> 为本次推进的游戏秒，
        /// 例如 StepMany(n) 后传 <c>GameClock.StepSeconds * n</c>；省略（&lt; 0）时取 GameClock.FrameScaledDt——它只在 GameClock 的帧推进里赋值，StepMany 之后恒为 0。
        /// 世界内采集要逐字节可复现：先 GameClock.ResetSession()，每帧 StepMany(n) 后 TickManual(dt, dt)。StepMany 推进 Ticks（GameSeconds / SmoothGameSeconds 照走），
        /// 但不动 GameClock 的累计量，所以 StepAlpha / Paused / Speed / SlowMotion 取的是活的 GameClock 状态——不 ResetSession 时两次采集的 StepAlpha 可能不同。
        /// </summary>
        public static void TickManual(float realDt, float gameDt = -1f)
        {
            // 先判 Current：没有战役时 WorldSimulation.AnyLoaded 会枚举空的 SortedDictionary（每次分配一个栈）
            PresentationHost.Tick(BuildFrame(realDt, gameDt < 0f ? GameClock.FrameScaledDt : gameDt,
                CampaignSession.Current != null && WorldSimulation.AnyLoaded));
        }

        /// <summary>拼一帧；同时做 PL-INF-07 重建检测（结果登记进宿主，宿主下一次 Tick 开头合并处理）。</summary>
        public static PresentationFrame BuildFrame(float realDt, float gameDt, bool inWorld)
        {
            DetectRebuild(inWorld);
            return new PresentationFrame(realDt, gameDt, GameClock.GameSeconds, GameClock.Ticks, GameClock.StepAlpha, GameClock.Paused,
                inWorld, WorldView.ObservedSiteId, WorldView.Camera, GameClock.EffectiveSpeed, GameClock.SlowMotionFactor,
                (GameClock.Ticks + (double)GameClock.StepAlpha) / GameClock.StepHz); // 同 BeltNetworkService 的 AnimationTime 写法
        }

        /// <summary>
        /// PL-INF-07：CampaignSession.Current 换引用（读档 / 新开局 / 回主菜单，含 null ↔ 非 null）或 InWorld 由 true 变 false（退出战役清场）→ Reload；
        /// 观察地点变了（ObserveSwitchCount 变化，或 ObservedSiteId 变化——全灭回家园不计数也要算）→ SiteSwitch。同帧多因由宿主合并成一次。
        /// shortcut: 主线没有读档事件（FG15-SYS-01 建读档入口后），只能按引用比较；若改成原地覆盖同一个 CampaignState 对象，这里要改订阅事件。
        /// </summary>
        private static void DetectRebuild(bool inWorld)
        {
            CampaignState session = CampaignSession.Current;
            if (!ReferenceEquals(session, _seenSession) || (_seenInWorld && !inWorld))
            {
                _seenSession = session;
                PresentationHost.RequestRebuild(RebuildReason.Reload);
            }
            _seenInWorld = inWorld;
            string site = WorldView.ObservedSiteId;
            int switches = WorldView.ObserveSwitchCount;
            if (switches != _seenSwitchCount || !string.Equals(site, _seenSiteId))
            {
                _seenSwitchCount = switches;
                _seenSiteId = site;
                PresentationHost.RequestRebuild(RebuildReason.SiteSwitch);
            }
        }
    }
}
