using System;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>一天里的阶段（日历 / HUD / 光照共用）。</summary>
    public enum DayPhase : byte
    {
        Day = 0,
        /// <summary>黄昏：入夜前 env.daynight.twilight_hours 游戏小时，光照从 1 降到 0。</summary>
        Dusk = 1,
        /// <summary>夜晚：光照 0，太阳能不发电（FGR-ENV-003）。</summary>
        Night = 2,
        /// <summary>黎明：夜晚结束后 env.daynight.twilight_hours 游戏小时，光照从 0 升到 1。</summary>
        Dawn = 3,
    }

    /// <summary>
    /// FG7-ENV-01（FG07 FGR-ENV-001 / 003，FG14 FGR-ARC-009）：昼夜。**不另造时钟**——完全由统一时钟（<see cref="GameClock"/>）的游戏秒算出，
    /// 是纯函数：同一游戏时刻结果相同，与帧率、倍速、暂停、镜头在哪无关；没有新的存档状态（游戏秒本身已在 <c>GameClockState</c> 里）。
    /// - 1 个游戏日 = 1x 下 clock.day_seconds（20 分钟）：夜晚从 env.daynight.night_start_hour 起持续 env.daynight.night_hours 游戏小时（初值 20:24 起 7.2 小时 = 6 分钟），
    ///   其余是白天（14 分钟，含前后各 env.daynight.twilight_hours 的黄昏 / 黎明过渡）。
    /// - 光照 0～1：白天 1、夜晚 0、黄昏 / 黎明平滑过渡。太阳能经 <see cref="PowerEnvironment.DaylightProvider"/> 接光照（FG-GAP-096），夜里发电为 0。
    /// - 画面亮度（<see cref="View.DayNightLighting"/>）= 夜间亮度下限（设置项）～1 之间按光照插值；所有 UI 不受影响。
    /// - <see cref="WorldStep"/>：每游戏秒检查一次是否跨过入夜 / 天亮（O(1)），跨过时版本号 +1、第一次入夜发引导钩子。
    /// </summary>
    public static class DayNightCycle
    {
        private static bool _loaded;
        private static double _nightStart = 20.4;
        private static double _nightHours = 7.2;
        private static double _twilight = 1.0;

        /// <summary>入夜 / 天亮的次数版本号（HUD / 日历 / 光照比较它决定要不要重画；也供自检断言“跨过了几次”）。</summary>
        public static int Revision { get; private set; }

        /// <summary>本会话里由 <see cref="WorldStep"/> 观察到的入夜次数（自检用）。</summary>
        public static int NightfallCount { get; private set; }

        /// <summary>昼夜是否已接到太阳能（<see cref="Install"/> 调过、且没被自检复位）。</summary>
        public static bool Installed => PowerEnvironment.DaylightProvider == (Func<CampaignState, float>)DaylightOf;

        /// <summary>夜晚开始的时刻（游戏小时）。</summary>
        public static double NightStartHour
        {
            get
            {
                EnsureTuning();
                return _nightStart;
            }
        }

        public static double NightHours
        {
            get
            {
                EnsureTuning();
                return _nightHours;
            }
        }

        public static double TwilightHours
        {
            get
            {
                EnsureTuning();
                return _twilight;
            }
        }

        /// <summary>夜晚结束（天开始亮）的时刻（游戏小时，0～24）。</summary>
        public static double NightEndHour => Wrap(NightStartHour + NightHours);

        /// <summary>重读 env.daynight.* 调参（表缺失 / 不合法时用规格初值并告警，不静默）。</summary>
        public static void ReloadTuning()
        {
            _loaded = true;
            // 表里是 float：20.4f 读成 double 是 20.39999…，按 1/10000 游戏小时取整，否则“20:24”会显示成“20:23”、入夜晚一游戏分钟。
            double start = Math.Round(Tuning("env.daynight.night_start_hour", 20.4f), 4);
            double hours = Math.Round(Tuning("env.daynight.night_hours", 7.2f), 4);
            double tw = Math.Round(Tuning("env.daynight.twilight_hours", 1.0f), 4);
            if (!(hours > 0 && hours < 24) || !(tw >= 0) || hours + 2 * tw >= 24 || !(start >= 0 && start < 24))
            {
                InvalidTuningCount++;
                Log.Warning($"[DayNightCycle] 昼夜调参不合法（入夜 {start}、夜长 {hours}、过渡 {tw}），改用规格初值 20.4 / 7.2 / 1。");
                start = 20.4;
                hours = 7.2;
                tw = 1.0;
            }
            _nightStart = start;
            _nightHours = hours;
            _twilight = tw;
        }

        private static void EnsureTuning()
        {
            if (!_loaded)
            {
                ReloadTuning();
            }
        }

        /// <summary>调参不合法、回退到规格初值的次数（自检读：回退时必须告警，不静默）。</summary>
        public static int InvalidTuningCount { get; private set; }

        /// <summary>自检：注入调参（返回 null 时照常读表），用来测不合法调参的回退。</summary>
        public static Func<string, float?> TuningOverrideForTests;

        private static float Tuning(string id, float fallback)
        {
            float? injected = TuningOverrideForTests?.Invoke(id);
            if (injected.HasValue)
            {
                return injected.Value;
            }
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            Log.Warning($"[DayNightCycle] 调参 {id} 缺失（fg.TbHomeTuning），暂用规格初值 {fallback}。");
            return fallback;
        }

        private static double Wrap(double h)
        {
            h %= 24.0;
            return h < 0 ? h + 24.0 : h;
        }

        /// <summary>从入夜时刻算起已经过了几个游戏小时（0～24）。</summary>
        /// 加 1e-9 小时：整点边界（20:24:00、03:36:00）的浮点误差不把那一刻算到前一个阶段（入夜 / 天亮恰好落在那一游戏秒上，与日历显示一致）。
        private static double SinceNightStart(double gameSeconds) => Wrap(GameClock.HourOfDay(gameSeconds) - NightStartHour + 1e-9);

        /// <summary>这一刻所处的阶段。</summary>
        public static DayPhase PhaseAt(double gameSeconds)
        {
            double since = SinceNightStart(gameSeconds);
            double night = NightHours;
            double tw = TwilightHours;
            if (since < night)
            {
                return DayPhase.Night;
            }
            if (since < night + tw)
            {
                return DayPhase.Dawn;
            }
            if (since >= 24.0 - tw)
            {
                return DayPhase.Dusk;
            }
            return DayPhase.Day;
        }

        public static bool IsNight(double gameSeconds) => PhaseAt(gameSeconds) == DayPhase.Night;

        /// <summary>这一刻的光照（0 = 夜晚 … 1 = 白天；黄昏 / 黎明用平滑曲线过渡，夜晚恒为 0）。</summary>
        public static float Daylight(double gameSeconds)
        {
            double since = SinceNightStart(gameSeconds);
            double night = NightHours;
            double tw = TwilightHours;
            if (since < night)
            {
                return 0f;
            }
            if (tw > 0 && since < night + tw)
            {
                return Smooth((since - night) / tw);
            }
            if (tw > 0 && since >= 24.0 - tw)
            {
                return Smooth((24.0 - since) / tw);
            }
            return 1f;
        }

        private static float Smooth(double x)
        {
            x = Math.Max(0.0, Math.Min(1.0, x));
            return (float)(x * x * (3.0 - 2.0 * x));
        }

        /// <summary>按存档里的游戏时钟算光照（太阳能用：只读游戏状态，与观察无关）。</summary>
        public static float DaylightOf(CampaignState state) => Daylight(state?.Clock != null ? state.Clock.GameSeconds : GameClock.GameSeconds);

        /// <summary>
        /// 下一次昼夜变化：现在是白天 / 黄昏 → 下一次入夜（<paramref name="toNight"/> = true）；现在是夜晚 / 黎明 → 夜晚结束（false）。
        /// 返回距离那一刻还有多少游戏秒（&gt; 0）。
        /// </summary>
        public static double NextChange(double gameSeconds, out bool toNight)
        {
            double since = SinceNightStart(gameSeconds);
            double night = NightHours;
            double hours;
            if (since < night)
            {
                toNight = false;
                hours = night - since;
            }
            else
            {
                toNight = true;
                hours = 24.0 - since;
            }
            return Math.Max(1e-6, hours) * GameClock.HourSeconds;
        }

        /// <summary>第 <paramref name="day"/> 日的夜晚：开始 / 结束的游戏秒（夜晚跨过午夜时结束在第二天）。</summary>
        public static void NightOfDay(int day, out double startSeconds, out double endSeconds)
        {
            startSeconds = GameClock.SecondsAt(day, NightStartHour);
            endSeconds = startSeconds + NightHours * GameClock.HourSeconds;
        }

        /// <summary>阶段名（文本键 env.phase.*）。</summary>
        public static string PhaseName(DayPhase p) => GameText.Get(PhaseKey(p));

        public static string PhaseKey(DayPhase p) => p switch
        {
            DayPhase.Dusk => "env.phase.dusk",
            DayPhase.Night => "env.phase.night",
            DayPhase.Dawn => "env.phase.dawn",
            _ => "env.phase.day",
        };

        /// <summary>阶段图标（字形，不只靠颜色区分，B15）：☀ 白天 → ◐ 黄昏 → ● 夜晚 → ◑ 黎明。
        /// 只用 UI 字体（NotoSansSC，无回退字体）里有的字形（☾ 不在字体里，会显示成缺字方框）；FgDayNightSelfCheck D2 逐字核对。</summary>
        public static string PhaseGlyph(DayPhase p) => p switch
        {
            DayPhase.Night => "●",
            DayPhase.Dusk => "◐",
            DayPhase.Dawn => "◑",
            _ => "☀",
        };

        /// <summary>正式入口（<c>GameRoot.Startup</c>）：把昼夜接到太阳能（FG-GAP-096）。幂等。</summary>
        public static void Install()
        {
            ReloadTuning();
            PowerEnvironment.DaylightProvider = DaylightOf;
        }

        /// <summary>
        /// 世界模拟每步调用（<c>WorldSimulation.StepOnce</c>）：只在整游戏秒检查一次（O(1)），跨过入夜 / 天亮时版本号 +1；第一次入夜发引导钩子。
        /// 只看步序号，与观察无关；没有存档状态（上一秒的阶段由时间直接算出）。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticks, int stepHz)
        {
            if (state == null || stepHz <= 0 || ticks <= 0 || ticks % stepHz != 0)
            {
                return;
            }
            double now = ticks / (double)stepHz;
            bool nightNow = IsNight(now);
            bool nightBefore = IsNight(now - 1.0);
            if (nightNow == nightBefore)
            {
                return;
            }
            Revision++;
            if (nightNow)
            {
                NightfallCount++;
                GuidanceHooks.Raise(GuidanceHooks.EnvFirstNight);
            }
        }

        /// <summary>自检之间复位（不改调参缓存以外的任何东西；太阳能接口由 <see cref="PowerEnvironment.ResetForTests"/> 复位）。</summary>
        public static void ResetForTests()
        {
            _loaded = false;
            TuningOverrideForTests = null;
            NightfallCount = 0;
            Revision++;
        }
    }
}
