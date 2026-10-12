using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Env
{
    /// <summary>日历时间轴上一条事件的种类（FG07 第 4 节“日历面板把静默夜、天气、已知突袭画在同一条时间轴上”）。</summary>
    public enum CalendarKind : byte
    {
        Night = 0,
        SilentNight = 1,
        Weather = 2,
        Raid = 3,
    }

    /// <summary>日历时间轴上的一条事件（游戏秒；<see cref="EndSeconds"/> ≤ <see cref="StartSeconds"/> 表示一个时刻而不是一段）。</summary>
    public struct CalendarEntry
    {
        public CalendarKind Kind;
        public double StartSeconds;
        public double EndSeconds;
        /// <summary>短标题（时间轴标记的悬停标题、HUD）。</summary>
        public string Title;
        /// <summary>一句话说明（事件列表与悬停正文）。</summary>
        public string Detail;
        /// <summary>来源的稳定编号（突袭计划号 / 第几夜），同一事件跨刷新不变。</summary>
        public string Id;
        /// <summary>只是预报（监听站破译 / 天气预报），还没确定发生。</summary>
        public bool Forecast;
    }

    /// <summary>一类日历事件的来源。静默夜（FG7-ENV-02）、天气（FG7-ENV-03）由各自的系统实现并在启动时 <see cref="CalendarService.Register"/>。</summary>
    public interface ICalendarSource
    {
        CalendarKind Kind { get; }

        /// <summary>把 [<paramref name="fromSeconds"/>, <paramref name="toSeconds"/>) 内（或与它重叠）的事件加进 <paramref name="into"/>。只读游戏状态。</summary>
        void Collect(CampaignState state, double fromSeconds, double toSeconds, List<CalendarEntry> into);
    }

    /// <summary>
    /// FG7-ENV-01（FG07 FGR-ENV-002、第 4 节；FG13 FGU-26 日历）：日历的数据层——把各来源的事件按时间排成一条时间轴，供世界时间条（HUD）与日历面板共用。
    /// 内置两个来源：夜晚（<see cref="DayNightCycle"/>）与已知突袭（突袭导演的预警条同一口径：已预警 / 集结 / 在路上 / 监听站已预报的计划）。
    /// 静默夜与天气的来源由 FG7-ENV-02 / FG7-ENV-03 注册（DEBT-FG7ENV01-01）；没注册时日历照常显示夜晚与突袭，并写明这两类还没有。
    /// 只读查询：不改任何状态、不进存档；O(来源数 + 事件数)，只在日历面板打开 / HUD 刷新键变化时调用。
    /// </summary>
    public static class CalendarService
    {
        private static readonly List<ICalendarSource> Sources = new List<ICalendarSource>(4);
        private static readonly List<CalendarEntry> Scratch = new List<CalendarEntry>(32);
        private static bool _builtIns;

        /// <summary>注册一个来源（同一实例只注册一次）。</summary>
        public static void Register(ICalendarSource source)
        {
            EnsureBuiltIns();
            if (source != null && !Sources.Contains(source))
            {
                Sources.Add(source);
            }
        }

        /// <summary>移除来源（自检复位用）。</summary>
        public static void Unregister(ICalendarSource source)
        {
            Sources.Remove(source);
        }

        private static void EnsureBuiltIns()
        {
            if (_builtIns)
            {
                return;
            }
            _builtIns = true;
            Sources.Add(new NightSource());
            Sources.Add(new RaidSource());
        }

        /// <summary>有没有这一类事件的来源（静默夜 / 天气在对应系统接入前没有）。</summary>
        public static bool HasSource(CalendarKind kind)
        {
            EnsureBuiltIns();
            foreach (ICalendarSource s in Sources)
            {
                if (s.Kind == kind)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>日历面板显示今天起几个游戏日（fg.TbHomeTuning env.calendar.days，初值 5）。</summary>
        public static int Days => Math.Max(1, Math.Min(14, GridContent.TryGetTuning("env.calendar.days", out float d) ? (int)Math.Round(d) : 5));

        /// <summary>收集 [from, to) 内的全部事件，按开始时刻、种类、编号排序。返回共享列表（调用方不要留着）。</summary>
        public static IReadOnlyList<CalendarEntry> Collect(CampaignState state, double fromSeconds, double toSeconds)
        {
            EnsureBuiltIns();
            Scratch.Clear();
            if (state == null || !(toSeconds > fromSeconds))
            {
                return Scratch;
            }
            foreach (ICalendarSource s in Sources)
            {
                s.Collect(state, fromSeconds, toSeconds, Scratch);
            }
            Scratch.Sort((a, b) =>
            {
                int c = a.StartSeconds.CompareTo(b.StartSeconds);
                if (c != 0)
                {
                    return c;
                }
                c = a.Kind.CompareTo(b.Kind);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });
            return Scratch;
        }

        /// <summary>从 <paramref name="nowSeconds"/> 起 <paramref name="horizonSeconds"/> 内第一条 <paramref name="kind"/> 事件（正在进行的也算）。</summary>
        public static bool TryNext(CampaignState state, CalendarKind kind, double nowSeconds, double horizonSeconds, out CalendarEntry entry)
        {
            foreach (CalendarEntry e in Collect(state, nowSeconds, nowSeconds + horizonSeconds))
            {
                if (e.Kind == kind && (e.EndSeconds > nowSeconds || e.StartSeconds >= nowSeconds))
                {
                    entry = e;
                    return true;
                }
            }
            entry = default;
            return false;
        }

        /// <summary>“X时Y分”短格式（HUD 用；向上取整到游戏分钟）。</summary>
        public static string ShortDuration(double seconds)
        {
            double minuteSeconds = GameClock.HourSeconds / 60.0;
            int minutes = Math.Max(1, (int)Math.Ceiling(Math.Max(0.0, seconds) / Math.Max(1e-6, minuteSeconds) - 1e-6));
            return GameText.Format("env.duration_short", (minutes / 60).ToString(CultureInfo.InvariantCulture), (minutes % 60).ToString("00", CultureInfo.InvariantCulture));
        }

        /// <summary>自检之间复位：去掉自检注册的来源，只留内置来源。</summary>
        public static void ResetForTests()
        {
            Sources.Clear();
            _builtIns = false;
            EnsureBuiltIns();
        }

        // ─────────────────────────────── 内置来源 ───────────────────────────────

        /// <summary>夜晚：每个游戏日一段（跨午夜），写明入夜与天亮时刻、夜里太阳能停发。</summary>
        private sealed class NightSource : ICalendarSource
        {
            public CalendarKind Kind => CalendarKind.Night;

            public void Collect(CampaignState state, double fromSeconds, double toSeconds, List<CalendarEntry> into)
            {
                int first = GameClock.DayOf(fromSeconds) - 1;
                int last = GameClock.DayOf(toSeconds);
                for (int day = first; day <= last; day++)
                {
                    DayNightCycle.NightOfDay(day, out double start, out double end);
                    if (end <= fromSeconds || start >= toSeconds)
                    {
                        continue;
                    }
                    into.Add(new CalendarEntry
                    {
                        Kind = CalendarKind.Night,
                        StartSeconds = start,
                        EndSeconds = end,
                        Id = "night:" + day.ToString(CultureInfo.InvariantCulture),
                        Title = GameText.Get("env.phase.night"),
                        Detail = GameText.Format("env.cal.night", GameClock.FormatHhMm(start), GameClock.FormatHhMm(end)),
                    });
                }
            }
        }

        /// <summary>已知突袭：与预警条同一口径（<see cref="RaidDirectorService.IncomingWaves"/>：已预警 / 集结 / 在路上 / 已到达 + 监听站已预报的计划中突袭）。</summary>
        private sealed class RaidSource : ICalendarSource
        {
            public CalendarKind Kind => CalendarKind.Raid;

            public void Collect(CampaignState state, double fromSeconds, double toSeconds, List<CalendarEntry> into)
            {
                int hz = Math.Max(1, GameClock.StepHz);
                long now = GameClock.TicksFor(Math.Max(0.0, fromSeconds));
                IReadOnlyList<RaidWaveView> waves = RaidDirectorService.IncomingWaves(state, now);
                for (int i = 0; i < waves.Count; i++)
                {
                    RaidWaveView v = waves[i];
                    // 已到达的写到达时刻（不晚于现在；签名稳定，日历不会每分钟重画）；还没到的写预计抵达。
                    double at = v.Arrived
                        ? (v.ArrivalTick >= 0 ? Math.Min(v.ArrivalTick / (double)hz, fromSeconds) : fromSeconds)
                        : v.ArrivalTick >= 0 ? v.ArrivalTick / (double)hz : -1;
                    if (at < 0 || at >= toSeconds)
                    {
                        continue;
                    }
                    string dir = RaidDirectorService.WaveDirectionText(v);
                    string detail = v.Arrived
                        ? GameText.Format("env.cal.raid_arrived", dir)
                        : v.PlannedOnly
                            ? GameText.Format("env.cal.raid_planned", RaidDirectorService.FactionText(v.Lead), v.Units, dir, GameClock.FormatDayTime(at))
                            : GameText.Format(v.Assembling ? "env.cal.raid_assembling" : "env.cal.raid_warned", dir, GameClock.FormatDayTime(at));
                    into.Add(new CalendarEntry
                    {
                        Kind = CalendarKind.Raid,
                        StartSeconds = at,
                        EndSeconds = v.Arrived ? double.MaxValue : 0,
                        Id = "raid:" + (v.Lead?.PlanId ?? i.ToString(CultureInfo.InvariantCulture)),
                        Title = GameText.Format("env.cal.raid_title", v.Units),
                        Detail = detail,
                        Forecast = v.PlannedOnly,
                    });
                }
            }
        }
    }
}
