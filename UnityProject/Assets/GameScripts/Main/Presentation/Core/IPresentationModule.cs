// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using UnityEngine;

namespace BinGames.Presentation
{
    /// <summary>重建原因：读档 / 切观察地点 / 浮动原点平移 / 画质或可访问性设置变化（细节协议归 PL-INF-07）。</summary>
    public enum RebuildReason
    {
        Reload,
        SiteSwitch,
        OriginShift,
        Settings,
    }

    /// <summary>
    /// 一帧的表现输入（只读快照，值类型、零分配）。由 GameLogic 侧 <c>PresentationHostBridge</c> 从 GameClock / WorldView / CampaignSession 拼好交给宿主；
    /// Lab 与采集脚本直接 new 一个合成帧。
    /// </summary>
    public readonly struct PresentationFrame
    {
        /// <summary>真实秒（不受暂停 / 倍速影响）。</summary>
        public readonly float RealDt;
        /// <summary>本帧推进的游戏秒（暂停为 0，倍速放大）。</summary>
        public readonly float GameDt;
        public readonly double GameSeconds;
        public readonly long SimTicks;
        /// <summary>两次模拟步之间的插值系数 [0,1]。</summary>
        public readonly float StepAlpha;
        public readonly bool Paused;
        /// <summary>有战役且有已载入地点（主菜单里为 false）。</summary>
        public readonly bool InWorld;
        public readonly string ObservedSiteId;
        public readonly Camera Camera;

        public PresentationFrame(float realDt, float gameDt, double gameSeconds, long simTicks, float stepAlpha, bool paused, bool inWorld,
            string observedSiteId, Camera camera)
        {
            RealDt = realDt;
            GameDt = gameDt;
            GameSeconds = gameSeconds;
            SimTicks = simTicks;
            StepAlpha = stepAlpha;
            Paused = paused;
            InWorld = inWorld;
            ObservedSiteId = observedSiteId;
            Camera = camera;
        }
    }

    /// <summary>
    /// 一个表现功能。<see cref="Id"/> 必须是 <see cref="PresentationFeatureRegistry"/> 里登记过的功能 ID；宿主总开关与该功能开关都开时才启用。
    /// 约定：OnDisable 必须销毁自己建的全部对象（宿主自检断言 [PresentationRoot] 下无残留）；只读 sim，不回写。
    /// </summary>
    public interface IPresentationModule
    {
        string Id { get; }
        void OnEnable();
        void OnDisable();
        void Tick(in PresentationFrame frame);
        void Rebuild(RebuildReason reason);
    }
}
