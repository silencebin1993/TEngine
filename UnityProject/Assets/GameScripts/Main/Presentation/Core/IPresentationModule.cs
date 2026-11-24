// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using UnityEngine;

namespace BinGames.Presentation
{
    /// <summary>
    /// 重建原因（PL-INF-07 协议）。同一帧收到多个原因时宿主合并：有 Reload 或 SiteSwitch 时只调一次（Reload 优先），它们视为全部重算，
    /// 模块收到这两种原因时必须连设置与原点一起重读；没有这两种时 OriginShift 与 Settings 互不包含，各调一次（先 OriginShift 后 Settings）。
    /// Reload = 读档 / 新开局 / 退出战役（CampaignSession.Current 换引用，或 InWorld 由 true 变 false）；SiteSwitch = 换观察地点；
    /// OriginShift = PresentationSpace.SetOrigin；Settings = 功能开关 / 画质 / 可访问性设置变化（卡片里的 FeatureToggle）。
    /// 状态分类约定：模块状态分两类——Transient（拖尾、粒子、印痕、弹簧余量等，Rebuild 时一律丢弃）与 Derived（能从 sim / 帧数据重算的，
    /// Rebuild 时标脏、下一次 Tick 从当前数据重算，结果与没重建过一致）。Rebuild 之后紧接着就是本帧 Tick（同一帧内）。
    /// </summary>
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
        /// <summary>本帧推进的游戏秒（暂停为 0，倍速放大）= GameClock.FrameScaledDt。只给装饰性动画用：丢步（每帧超 12 步）或步内自动暂停时它与 sim 实际推进量不一致，
        /// 要跟单位运动同步的用 <see cref="SmoothGameSeconds"/> 的差值。</summary>
        public readonly float GameDt;
        /// <summary>模拟步上的游戏秒（= SimTicks / StepHz，只在模拟步上跳）：要跟 sim 逐步对齐的逻辑用（SimStep 类）。</summary>
        public readonly double GameSeconds;
        /// <summary>PL-INF-07：连续的游戏秒（= (SimTicks + StepAlpha) / StepHz），每帧都变、暂停停、倍速 / 慢放按游戏时间走，与单位插值位置同一条时间轴。
        /// VAT 与 Game 类循环动画一律取它（取 GameSeconds 在 0.5x / 慢放下会按模拟步阶梯跳）。</summary>
        public readonly double SmoothGameSeconds;
        public readonly long SimTicks;
        /// <summary>两次模拟步之间的插值系数 [0,1]。</summary>
        public readonly float StepAlpha;
        public readonly bool Paused;
        /// <summary>有战役且有已载入地点（主菜单里为 false）。</summary>
        public readonly bool InWorld;
        public readonly string ObservedSiteId;
        public readonly Camera Camera;
        /// <summary>PL-INF-07：本帧生效的倍速档（GameClock.EffectiveSpeed，接入时恒 1）。</summary>
        public readonly float Speed;
        /// <summary>PL-INF-07：本帧的慢放倍率（GameClock.SlowMotionFactor，没有慢放 = 1）。</summary>
        public readonly float SlowMotion;

        public PresentationFrame(float realDt, float gameDt, double gameSeconds, long simTicks, float stepAlpha, bool paused, bool inWorld,
            string observedSiteId, Camera camera, float speed = 1f, float slowMotion = 1f, double smoothGameSeconds = double.NaN)
        {
            SmoothGameSeconds = double.IsNaN(smoothGameSeconds) ? gameSeconds : smoothGameSeconds;
            RealDt = realDt;
            GameDt = gameDt;
            GameSeconds = gameSeconds;
            SimTicks = simTicks;
            StepAlpha = stepAlpha;
            Paused = paused;
            InWorld = inWorld;
            ObservedSiteId = observedSiteId;
            Camera = camera;
            Speed = speed;
            SlowMotion = slowMotion;
        }

        /// <summary>PL-INF-07 手动时钟（无世界）：纯合成帧（不读 Unity Time、不读 GameClock，InWorld=false、SimTicks=0），只给不依赖世界的纯模块测试，
        /// 按固定 dt 逐帧推进两次结果逐字节相同；GameSeconds = SmoothGameSeconds = gameSeconds。世界内采集见 PresentationHostBridge.TickManual。</summary>
        public static PresentationFrame Synthetic(float dt, double gameSeconds) =>
            new PresentationFrame(dt, dt, gameSeconds, 0, 0f, false, false, null, null);
    }

    /// <summary>PL-INF-07：模块动画走哪条时间（Game = 暂停停、倍速 / 慢放按游戏时间走；Real = 真实时间，震屏 / 镜头推动；SimStep = 只在模拟步上变，用 StepAlpha 插值）。</summary>
    public enum TimeDomain
    {
        Game,
        Real,
        SimStep,
    }

    /// <summary>PL-INF-07：每个 PL 模块类必须声明时间类别（PresentationValidate.ClockManualDeterminism 段检查非编辑器程序集里的全部实现）。</summary>
    [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
    public sealed class PresentationTimeAttribute : System.Attribute
    {
        public TimeDomain Domain { get; }

        public PresentationTimeAttribute(TimeDomain domain) => Domain = domain;
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
        /// <summary>宿主在本帧 Tick 之前调（同帧合并规则见 <see cref="RebuildReason"/>），此时拿不到本帧 frame：丢弃 Transient 状态、把 Derived 状态标脏。
        /// Reload 一律丢掉全部挂在世界上的对象，下一次 Tick 里 frame.InWorld 为真时再重建（退出战役那帧 InWorld 为假，就不建）——别依赖上一帧的 InWorld。</summary>
        void Rebuild(RebuildReason reason);
    }
}
