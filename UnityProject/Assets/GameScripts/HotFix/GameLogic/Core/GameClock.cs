using System;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Core
{
    /// <summary>
    /// FG0-ARCH-01（FG14 FGR-ARC-009 统一游戏时钟、FGR-ARC-010 固定步长；FG07 FGR-ENV-001 时间单位）：整个世界唯一的游戏时钟。
    ///
    /// - **唯一的暂停与倍速**：暂停、0.5x / 1x / 2x / 3x 只在这里；家园、远征地点、行进中的突袭、生产、研究、解析……所有计时系统都由
    ///   <see cref="Campaign.WorldSim.WorldSimulation"/> 按本时钟给出的固定步驱动，暂停时同时停、倍速时同比例加速。
    /// - **固定步长**：每步 1 / clock.sim_step_hz 游戏秒。倍速只改变每一帧跑几步，不改变每步的 dt，所以同一段游戏时间在 0.5x～3x 下
    ///   执行的是完全相同的步序列，结果与 1x 逐字段一致（FGR-ARC-009“结果与 1x 一致”）。
    /// - **接入锁 1x**：接入（直控）一台机器时整个世界固定 1x（Demo 的 directLocked 语义推广到全世界：世界只有一个时钟，不能一边 3x 一边 1x）。
    /// - **游戏日**：第 N 日 HH:MM 由 <see cref="GameSeconds"/> 与 clock.day_seconds / clock.start_hour 推导（1x 下 1 游戏日 = 20 分钟）。
    /// - 旧入口 <see cref="StrategyClock"/> 保留为本类的外观（旧细胞阶段与既有自检仍按它读写倍率）。
    ///
    /// 每帧开销 O(1)（与实体数无关）；状态随存档往返（<see cref="GameClockState"/>：步数、步长频率、游戏秒、第几日）。
    /// </summary>
    public static class GameClock
    {
        /// <summary>FGR-ENV-001 / FGR-ARC-009 规定的倍速档（与输入动作 SpeedHalf / SpeedNormal / SpeedDouble / SpeedTriple 一一对应，
        /// 属于规格结构而不是调参数值，所以写在代码里；每档的键位在 fg.TbInputAction）。</summary>
        public static readonly float[] Speeds = { 0.5f, 1f, 2f, 3f };

        public static float Speed { get; private set; } = 1f;
        public static bool Paused { get; private set; }
        /// <summary>当前是否处于接入（直控）视角——整个世界锁 1x。由全局镜头每帧写入。</summary>
        public static bool DirectLocked { get; private set; }
        /// <summary>本帧实际生效的倍率（接入时恒为 1）。</summary>
        public static float EffectiveSpeed => DirectLocked ? 1f : Speed;

        /// <summary>已执行的固定模拟步数（确定性时间轴）。</summary>
        public static long Ticks { get; private set; }
        public static int StepHz { get; private set; } = 60;
        public static float StepSeconds => 1f / StepHz;
        /// <summary>已模拟的游戏秒数（1x 下等于真实秒数）。</summary>
        public static double GameSeconds => Ticks / (double)StepHz;

        /// <summary>本帧按倍率缩放后的游戏时间（暂停为 0）。只给"玩家本帧的实时输入"（接入移动、交互进度）用；
        /// 一切会被观察影响的模拟都必须走固定步（<see cref="Campaign.WorldSim.WorldSimulation"/>）。</summary>
        public static float FrameScaledDt { get; private set; }
        public static int LastFrameSteps { get; private set; }
        /// <summary>因单帧步数上限而丢弃的步数累计（世界短暂变慢，不跳步；性能证据用）。</summary>
        public static long DroppedSteps { get; private set; }
        /// <summary>暂停 / 倍速变化计数（HUD 与自检用）。</summary>
        public static int Revision { get; private set; }

        private static double _accumulator;
        private static int _maxStepsPerFrame = 12;
        private static float _maxFrameSeconds = 0.25f;
        private static double _daySeconds = 1200.0;
        private static double _startHour = 6.0;

        // ── 暂停与倍速 ──────────────────────────────────────────────────────────

        public static void SetSpeed(float multiplier)
        {
            float nearest = Speeds[0];
            float best = Math.Abs(multiplier - nearest);
            foreach (float s in Speeds)
            {
                float d = Math.Abs(multiplier - s);
                if (d < best)
                {
                    nearest = s;
                    best = d;
                }
            }
            if (!nearest.Equals(Speed))
            {
                Speed = nearest;
                Revision++;
            }
        }

        public static void CycleSpeed()
        {
            int index = Array.IndexOf(Speeds, Speed);
            SetSpeed(Speeds[(index < 0 ? 0 : index + 1) % Speeds.Length]);
        }

        public static void SetPaused(bool paused)
        {
            if (Paused != paused)
            {
                Paused = paused;
                Revision++;
            }
        }

        public static void TogglePause() => SetPaused(!Paused);

        public static void SetDirectLocked(bool locked)
        {
            if (DirectLocked != locked)
            {
                DirectLocked = locked;
                Revision++;
            }
        }

        // ── 推进 ────────────────────────────────────────────────────────────────

        /// <summary>本帧应执行多少个固定模拟步。暂停时 0（累计量保留，继续后不丢零头）；超长帧按 clock.max_frame_seconds 截断；
        /// 步数超过 clock.max_steps_per_frame 时丢弃多余部分（记入 <see cref="DroppedSteps"/>）。
        /// <paramref name="tickLimit"/>：最多推进到第几步（自检用来精确停在同一步；正式流程不传）。</summary>
        public static int Advance(float realDt, long tickLimit = long.MaxValue)
        {
            if (Paused)
            {
                FrameScaledDt = 0f;
                LastFrameSteps = 0;
                return 0;
            }
            float dt = realDt < 0f ? 0f : (realDt > _maxFrameSeconds ? _maxFrameSeconds : realDt);
            FrameScaledDt = dt * EffectiveSpeed;
            _accumulator += FrameScaledDt;
            double step = 1.0 / StepHz;
            int n = (int)Math.Floor(_accumulator / step + 1e-9);
            if (n > _maxStepsPerFrame)
            {
                DroppedSteps += n - _maxStepsPerFrame;
                n = _maxStepsPerFrame;
                _accumulator = step * n; // 丢弃多余部分：下面减掉 n 步后剩 0。
            }
            if (tickLimit != long.MaxValue && Ticks + n > tickLimit)
            {
                n = (int)Math.Max(0, tickLimit - Ticks);
            }
            _accumulator = Math.Max(0.0, _accumulator - n * step);
            LastFrameSteps = n;
            return n;
        }

        /// <summary>本帧已由 <see cref="Advance"/> 批出、但因为步内触发了暂停（如紧急通知自动暂停）而没有执行的步数退回累计量：
        /// 暂停停在触发它的那一步，与帧率 / 倍速无关；继续后这些步照常执行，不丢时间。</summary>
        public static void RefundSteps(int count)
        {
            if (count <= 0)
            {
                return;
            }
            _accumulator += count * (1.0 / StepHz);
            LastFrameSteps = Math.Max(0, LastFrameSteps - count);
        }

        /// <summary>一个模拟步执行完毕：推进时间轴并写回存档域（每步写 4 个字段，O(1)）。</summary>
        public static void CommitStep(CampaignState state)
        {
            Ticks++;
            WriteTo(state);
        }

        public static void WriteTo(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            state.Clock ??= new GameClockState();
            state.Clock.Ticks = Ticks;
            state.Clock.StepHz = StepHz;
            state.Clock.GameSeconds = GameSeconds;
            state.Clock.Day = DayOf(GameSeconds);
            // 战役的“游戏内经过时长”（存档卡“游戏时长”、任务日志“战役时间”、目标完成时刻、胜利页完成时间）此前没有任何写入方，
            // 恒为 0；统一时钟接入后按游戏时间写（与倍速无关、与观察无关，确定性）。
            state.PlaySeconds = (float)GameSeconds;
        }

        // ── 会话 ────────────────────────────────────────────────────────────────

        /// <summary>接到一个战役（新建或读档）：读调参，从存档恢复时间轴。存档的步长频率与当前不同时按游戏秒换算（不丢时间）。
        /// 暂停与倍速不进存档：读档后从 1x、未暂停开始（与 Demo 回菜单复位速度的纪律一致）。</summary>
        public static void Bind(CampaignState state)
        {
            ReloadTuning();
            ResetTransient();
            Ticks = 0;
            GameClockState c = state?.Clock;
            if (c != null)
            {
                if (c.StepHz == StepHz && c.Ticks > 0)
                {
                    Ticks = c.Ticks;
                }
                else if (c.GameSeconds > 0)
                {
                    Ticks = (long)Math.Round(c.GameSeconds * StepHz);
                }
            }
            WriteTo(state);
        }

        /// <summary>离开世界（回主菜单）或自检之间：时间轴归零，速度 1x、未暂停、不锁。</summary>
        public static void ResetSession()
        {
            ResetTransient();
            Ticks = 0;
        }

        private static void ResetTransient()
        {
            Speed = 1f;
            Paused = false;
            DirectLocked = false;
            _accumulator = 0;
            FrameScaledDt = 0f;
            LastFrameSteps = 0;
            Revision++;
        }

        /// <summary>重读 clock.* 调参（表缺失时用规格初值并告警，不静默）。</summary>
        public static void ReloadTuning()
        {
            StepHz = Math.Max(1, (int)Math.Round(Tuning("clock.sim_step_hz", 60f)));
            _maxStepsPerFrame = Math.Max(1, (int)Math.Round(Tuning("clock.max_steps_per_frame", 12f)));
            _maxFrameSeconds = Math.Max(0.02f, Tuning("clock.max_frame_seconds", 0.25f));
            _daySeconds = Math.Max(1.0, Tuning("clock.day_seconds", 1200f));
            _startHour = Tuning("clock.start_hour", 6f);
        }

        public static float TuningOr(string id, float fallback) => Tuning(id, fallback);

        private static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            Log.Warning($"[GameClock] 调参 {id} 缺失（fg.TbHomeTuning），暂用规格初值 {fallback}。");
            return fallback;
        }

        // ── 游戏日 ──────────────────────────────────────────────────────────────

        public static double DaySeconds => _daySeconds;

        /// <summary>第几个游戏日（从 1 起）。</summary>
        public static int DayOf(double gameSeconds)
        {
            double sinceMidnight = gameSeconds + _startHour / 24.0 * _daySeconds;
            return (int)Math.Floor(sinceMidnight / _daySeconds) + 1;
        }

        /// <summary>当天第几分钟（0～1439；1x 下 1 游戏小时 = day_seconds / 24 真实秒）。</summary>
        public static int MinuteOfDay(double gameSeconds)
        {
            double sinceMidnight = gameSeconds + _startHour / 24.0 * _daySeconds;
            double frac = sinceMidnight / _daySeconds - Math.Floor(sinceMidnight / _daySeconds);
            return Math.Min(24 * 60 - 1, (int)Math.Floor(frac * 24 * 60 + 1e-6));
        }

        public static string FormatHhMm(double gameSeconds)
        {
            int m = MinuteOfDay(gameSeconds);
            return (m / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (m % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>“第 N 日 HH:MM”（FG13：时间写"第 N 日 HH:MM"）。</summary>
        public static string FormatDayTime(double gameSeconds) =>
            GameText.Format("ui.world.day_time", DayOf(gameSeconds).ToString(CultureInfo.InvariantCulture), FormatHhMm(gameSeconds));

        /// <summary>一段游戏时长换成"X 小时 Y 分（游戏时间）"（ETA 用，向上取整到游戏分钟；1 游戏分钟 = day_seconds / 1440 真实秒）。</summary>
        public static string FormatGameDuration(double seconds)
        {
            int minutes = Math.Max(1, (int)Math.Ceiling(Math.Max(0.0, seconds) / (_daySeconds / (24.0 * 60.0)) - 1e-6));
            return GameText.Format("ui.world.eta", (minutes / 60).ToString(CultureInfo.InvariantCulture), (minutes % 60).ToString(CultureInfo.InvariantCulture));
        }
    }
}
