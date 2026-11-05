using System;
using System.Diagnostics;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using UnityEditor;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>运行器在纯等待步骤（<see cref="JourneyStep.PureWait"/>）里调用的快进器；自检注入假的。</summary>
    public interface IJourneyFastForward
    {
        /// <summary>目标倍数（相对旅程此刻本来的游戏速度）。</summary>
        double Multiplier { get; }
        void Begin();
        /// <summary>
        /// 每次驱动、在步骤 Tick 之前调用一次。每多走一步就调一次 <paramref name="settled"/>（看这一步的条件），返回 true 就停——
        /// 条件满足的那一刻不会多走（与正常节奏一样，后面的点击落在同一个游戏时刻附近）。
        /// 返回 true = 额外的步里世界刚暂停（步内自动暂停）：这一次不看条件，下一次再看。
        /// </summary>
        bool Pump(Func<bool> settled);
        /// <summary>结束这一段：返回（快进期间走过的游戏秒，用掉的真实秒，按原倍速本该用的真实秒）。</summary>
        (double game, double real, double normal) End();
    }

    /// <summary>
    /// FG-EMG-3 旅程空等快进（默认关闭；环境变量 JOURNEY_FASTFORWARD=&lt;倍数&gt;，未设 / ≤1 = 关）。
    ///
    /// 只在标了 <see cref="JourneyStep.PureWait"/> 的步骤里、每次驱动额外执行 (倍数−1) × 本该走的固定模拟步——
    /// 走的就是正式的 <see cref="WorldSimulation.StepMany"/>（与 <see cref="WorldSimulation.Frame"/> 同一个 StepOnce，不跳逻辑、不改状态）。
    /// 不碰 <see cref="GameClock.Speed"/>：步骤里读倍速 / 按倍速键的逻辑看到的仍是玩家设的档位；暂停、接入（锁 1x）、慢放中不额外走。
    /// 每多走一步就看一次这一步的条件，满足就停（不多走）。每次驱动最多多走 <see cref="MaxGameSecondsPerPump"/> 游戏秒、
    /// 最多花 <see cref="BudgetMs"/> 毫秒（批处理无渲染下帧时间不被拖爆）——达不到的倍数不硬追，结束行写实际倍数。
    /// </summary>
    public sealed class JourneyFastForward : IJourneyFastForward
    {
        public const string EnvVar = "JOURNEY_FASTFORWARD";
        /// <summary>每次驱动额外步数的真实时间预算（毫秒）。</summary>
        public const double BudgetMs = 40;
        /// <summary>每次驱动最多额外走多少游戏秒（条件满足那一刻最多多走这么多）。</summary>
        public const double MaxGameSecondsPerPump = 2;

        /// <summary>快进中（给帧耗时采样跳过这段：额外步数会把帧拉长，不代表游戏帧耗时）。</summary>
        public static bool Active { get; private set; }

        public double Multiplier { get; }

        private double _lastPump;
        private long _lastTicks;
        private double _owed;
        private double _start;
        private double _game;
        private double _normal;

        public JourneyFastForward(double multiplier)
        {
            Multiplier = multiplier;
        }

        /// <summary>按环境变量建；关闭时返回 null（运行器什么都不做，行为与没有本功能时逐字相同）。</summary>
        public static JourneyFastForward FromEnvironment()
        {
            string raw = Environment.GetEnvironmentVariable(EnvVar);
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 1 ? new JourneyFastForward(m) : null;
        }

        public void Begin()
        {
            _start = _lastPump = EditorApplication.timeSinceStartup;
            _lastTicks = GameClock.Ticks;
            _owed = _game = _normal = 0;
            Active = true;
        }

        public bool Pump(Func<bool> settled)
        {
            double now = EditorApplication.timeSinceStartup;
            double dt = Math.Min(0.25, Math.Max(0, now - _lastPump));
            _lastPump = now;
            Account();
            CampaignState s = CampaignSession.Current;
            if (!EditorApplication.isPlaying || s == null || !WorldSimulation.AnyLoaded || GameClock.Paused || GameClock.DirectLocked || GameClock.SlowMotionFactor < 1f)
            {
                _normal += GameClock.Paused ? dt : 0; // 暂停的那段真实时间不快进时同样要花
                _owed = 0;
                return false;
            }
            double owed = _owed + dt * GameClock.EffectiveSpeed * GameClock.StepHz * (Multiplier - 1);
            int want = (int)Math.Min(owed, MaxGameSecondsPerPump * GameClock.StepHz);
            var sw = Stopwatch.StartNew();
            int done = 0;
            while (done < want && sw.Elapsed.TotalMilliseconds < BudgetMs)
            {
                WorldSimulation.StepMany(1);
                done++;
                if (GameClock.Paused || !ReferenceEquals(s, CampaignSession.Current))
                {
                    break; // 步内自动暂停 / 换了战役状态：与 Frame 一样停在这一步
                }
                if (settled())
                {
                    break; // 条件满足：停在这一步，不多走
                }
            }
            _owed = done == want ? Math.Min(owed - want, 1.0) : 0; // 没走完的（预算到了 / 停下了）丢掉，不攒欠账
            Account();
            // 正常时自动暂停发生在玩家循环里，步骤下一次驱动才看到、按暂停键继续；这里暂停发生在驱动里，同样留到下一次再看，
            // 否则“继续”的按键与下一步的按键挤在相邻两帧（试点实测 ra_pause 因此多重试一次）。
            return GameClock.Paused;
        }

        /// <summary>把上次记账以来走过的步（正常帧的 + 额外的）计入游戏秒与“原倍速下本该用的真实秒”。</summary>
        private void Account()
        {
            long t = GameClock.Ticks;
            if (t > _lastTicks && GameClock.EffectiveSpeed > 0)
            {
                double sec = (t - _lastTicks) / (double)GameClock.StepHz;
                _game += sec;
                _normal += sec / GameClock.EffectiveSpeed;
            }
            _lastTicks = t; // 读档后时间轴换了（变小）：从新的接着记
        }

        public (double game, double real, double normal) End()
        {
            Account();
            Active = false;
            return (_game, EditorApplication.timeSinceStartup - _start, _normal);
        }
    }
}
