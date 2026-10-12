using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG7-ENV-02（FG07 FGR-ENV-010～015，FGU-29）：静默夜。
    /// - 周期（FGR-ENV-010）：每 N 个游戏日（难度 fg.TbRaidDifficulty.silentNightDays，标准 5）的夜里一次，持续 env.silent.duration_hours（4）游戏小时，
    ///   开始前 env.silent.warning_hours（1.2）游戏小时预警；日历 / 世界时间条经日历来源始终看得到倒计时。
    ///   排定时把预警 / 开始 / 结束算成统一时钟的整数步写进存档（<see cref="SilentNightState"/>），之后调参或难度变化只影响再下一次。
    /// - 期间规则（FGR-ENV-011，逐项）：接入不可用、开始时信号弹回核心（<see cref="SignalLinkService.OnSilentNightStarted"/> + 判定入口 <see cref="SignalUplinkService.SilentNightProvider"/>）；
    ///   远程命令（编队命令 / 右键派工 / 驻防调动）不可用（<see cref="SignalCoverageService.CanReceiveCommand"/>、<see cref="MachineRoster"/>）；
    ///   接入口全空、炮塔与靶场投影弹回（同一判定入口）；已有工作单与常驻规则继续执行（不拦，静默夜预案经 <see cref="StandingRuleService.SilentNightWindowProvider"/> 触发）；
    ///   新的施工规划可以放虚影，结束后才开工（<see cref="OnBuildOrderCreated"/> / <see cref="IsConstructionHeld"/>）；修改规则 / 配方 / 优先级被拒并写明原因（<see cref="EditLocked"/>）；
    ///   镜头、面板、统计照常；突袭必定发生（<see cref="RaidDirectorService.OnSilentNightStarted"/>）。
    /// - 远征（FGR-ENV-013）：开始时在远征地点的机器进入安全模式、继续执行最后的命令（命令收不到）；远征准备时预计跨过静默夜给提示（<see cref="ExpeditionCrossText"/>）。
    /// - 撑过去（FGR-ENV-014）：结束时按种子在家园地面放几处风暴残骸（fg.TbStormDebris），开系统搬运单让机器搬回；发一条总结通知（击退 / 损失 / 捡到）。
    /// - 首次（FGR-ENV-015）：预警通知本身就是非阻塞说明（会发生什么、要准备什么），另发引导钩子（引导内容在 FG15-UX-04）。
    /// 只在世界模拟步里推进（<see cref="WorldStep"/>，O(1)；开始 / 结束那一步 O(机器数 + 结算条数)），只看步序号与存档状态，观察与否、倍速、暂停结果一致（D-06 / FGR-BASE-021）。
    /// </summary>
    public static class SilentNightService
    {
        /// <summary>静默夜期间新放的施工单的等待原因码。</summary>
        public const string HoldReason = "silent-night-hold";

        /// <summary>静默夜状态变化（预警 / 开始 / 结束）的事件，参数 = 是否正在进行。</summary>
        public const string ChangedEvent = "SilentNight.Changed";

        private static bool _installed;
        private static readonly SilentCalendarSource Source = new SilentCalendarSource();

        /// <summary>预警 / 开始 / 结束的版本号（HUD 横幅、光照、日历据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        // ── 统计（自检读）──
        public static int WarnCount { get; private set; }
        public static int StartCount { get; private set; }
        public static int EndCount { get; private set; }
        public static int RejectCount { get; private set; }
        public static string LastReject { get; private set; } = string.Empty;
        public static int SkippedCount { get; private set; }

        /// <summary>正式入口（<c>GameRoot.Startup</c>）已经接上。</summary>
        public static bool Installed => _installed;

        /// <summary>正式入口：接上接入判定、常驻规则时间窗、日历来源，世界模拟开始按存档推进静默夜。幂等。</summary>
        public static void Install()
        {
            _installed = true;
            SignalUplinkService.SilentNightProvider = IsActiveNow;
            StandingRuleService.SilentNightWindowProvider = WindowOf;
            CalendarService.Register(Source);
            Revision++;
        }

        /// <summary>自检之间复位（断开所有接入点，不动存档）。</summary>
        public static void ResetForTests()
        {
            _installed = false;
            if (SignalUplinkService.SilentNightProvider == (Func<bool>)IsActiveNow)
            {
                SignalUplinkService.SilentNightProvider = null;
            }
            if (StandingRuleService.SilentNightWindowProvider == (Func<CampaignState, (long, long)>)WindowOf)
            {
                StandingRuleService.SilentNightWindowProvider = null;
            }
            CalendarService.Unregister(Source);
            _skipRaidOnce = false;
            WarnCount = StartCount = EndCount = RejectCount = SkippedCount = 0;
            LastReject = string.Empty;
            Revision++;
        }

        // ── 调参（fg.TbHomeTuning env.silent.*）──
        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static double StartOffsetHours => Math.Max(0.0, Tuning("env.silent.start_offset_hours", 1.6f));
        public static double DurationHours => Math.Max(0.05, Tuning("env.silent.duration_hours", 4f));
        public static double WarningHours => Math.Max(0.0, Tuning("env.silent.warning_hours", 1.2f));
        public static double DimRampHours => Math.Max(0.01, Tuning("env.silent.dim_ramp_hours", 0.25f));
        public static double ExpeditionEstimateHours => Math.Max(0.0, Tuning("env.silent.expedition_estimate_hours", 12f));

        /// <summary>间隔（游戏日，按当前难度）。</summary>
        public static int IntervalDays(CampaignState s) => Math.Max(1, DifficultyService.SilentNightDays(s));

        // ── 查询 ─────────────────────────────────────────────────────────────────

        public static SilentNightState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            s.Weather ??= new WeatherState();
            return s.Weather.SilentNight ??= new SilentNightState();
        }

        public static bool IsActive(CampaignState s) => s?.Weather?.SilentNight != null && s.Weather.SilentNight.Active;

        /// <summary>当前战役此刻是否在静默夜里（接入判定入口用）。</summary>
        public static bool IsActiveNow() => IsActive(CampaignSession.Current);

        /// <summary>静默夜剩余游戏秒（不在静默夜 = 0）。</summary>
        public static double RemainingSeconds(CampaignState s)
        {
            SilentNightState d = s?.Weather?.SilentNight;
            if (d == null || !d.Active)
            {
                return 0;
            }
            return Math.Max(0, (d.EndTick - GameClock.Ticks) / (double)Math.Max(1, GameClock.StepHz));
        }

        /// <summary>第 <paramref name="day"/> 日夜里那次静默夜的预警 / 开始 / 结束（整数步）。</summary>
        public static void PlanTicks(int day, out long warn, out long start, out long end)
        {
            DayNightCycle.NightOfDay(day, out double nightStart, out _);
            double h = GameClock.HourSeconds;
            double s = nightStart + StartOffsetHours * h;
            start = GameClock.TicksFor(s);
            end = GameClock.TicksFor(s + DurationHours * h);
            warn = GameClock.TicksFor(s - WarningHours * h);
        }

        /// <summary>当前或下一次静默夜的时间窗（常驻规则“静默夜预案”用；没排好返回 (-1, -1)）。</summary>
        public static (long Start, long End) WindowOf(CampaignState s)
        {
            SilentNightState d = s?.Weather?.SilentNight;
            if (d == null || d.NextDay <= 0 || d.StartTick < 0 || d.EndTick <= d.StartTick)
            {
                return (-1, -1);
            }
            return (d.StartTick, d.EndTick);
        }

        /// <summary>画面压暗系数（0～1，淡入淡出；纯表现，读统一时钟）。</summary>
        public static float DimFactor(CampaignState s, double nowSeconds)
        {
            SilentNightState d = s?.Weather?.SilentNight;
            if (d == null || !d.Active)
            {
                return 0f;
            }
            double hz = Math.Max(1, GameClock.StepHz);
            double ramp = DimRampHours * GameClock.HourSeconds;
            double inF = (nowSeconds - d.StartTick / hz) / ramp;
            double outF = (d.EndTick / hz - nowSeconds) / ramp;
            return (float)Math.Max(0.0, Math.Min(1.0, Math.Min(inF, outF)));
        }

        // ── 期间规则：拒绝修改（FGR-ENV-011“修改规则、配方、优先级 | 不可用（界面上显示原因）”）──

        /// <summary>
        /// 静默夜期间拒绝一次玩家修改：返回 true 并给出原因（<paramref name="reasonKey"/> = env.silent.reject.*，{0} = 剩余时间），同时出拒绝音与字幕（调用方没显示 message 时玩家也看得到原因，B06）。
        /// 只拦玩家入口；常驻规则自己执行时不走这里（规则继续执行）。
        /// </summary>
        public static bool EditLocked(CampaignState s, string reasonKey, out string message)
        {
            message = null;
            if (!IsActive(s))
            {
                return false;
            }
            message = GameText.Format(reasonKey, CalendarService.ShortDuration(RemainingSeconds(s)));
            RejectCount++;
            LastReject = message;
            FeedbackCues.Raise(FeedbackCueId.Denied, message);
            return true;
        }

        public static bool RulesLocked(CampaignState s, out string message) => EditLocked(s, "env.silent.reject.rules", out message);
        public static bool RecipeLocked(CampaignState s, out string message) => EditLocked(s, "env.silent.reject.recipe", out message);
        public static bool PriorityLocked(CampaignState s, out string message) => EditLocked(s, "env.silent.reject.priority", out message);
        public static bool GarrisonLocked(CampaignState s, out string message) => EditLocked(s, "env.silent.reject.garrison", out message);

        /// <summary>剩余时间的玩家文字（拒绝原因 env.silent.reject.* 里的 {0}；复制设置 / 撤销重做被拒时组 GridReason 用）。</summary>
        public static string RemainingText(CampaignState s) => CalendarService.ShortDuration(RemainingSeconds(s));

        // ── 期间规则：新的施工规划结束后才开工 ──

        /// <summary>开施工单时调用（<c>HomeValleyWorkOrders.Append</c>）：静默夜期间新开的施工单记进暂缓名单；开单时就点了机器的放回待分配池（那也是一条远程命令）。</summary>
        public static void OnBuildOrderCreated(CampaignState s, WorkOrderRecord order)
        {
            if (order == null || order.Kind != WorkOrderKind.Build || !IsActive(s))
            {
                return;
            }
            SilentNightState d = StateOf(s);
            var list = new List<string>(d.HeldOrderIds ?? Array.Empty<string>());
            if (!list.Contains(order.WorkOrderId))
            {
                list.Add(order.WorkOrderId);
                d.HeldOrderIds = list.ToArray();
            }
            if (order.State == WorkOrderState.Reserved && order.AssignedMachineLogicId > 0)
            {
                order.AssignedMachineLogicId = 0;
                order.State = WorkOrderState.Ready;
            }
        }

        /// <summary>这张施工单是不是静默夜期间新放的（结束前不开工）。常驻规则派的（自动重建）不算——规则继续执行。</summary>
        public static bool IsConstructionHeld(CampaignState s, WorkOrderRecord order)
        {
            if (order == null || order.Kind != WorkOrderKind.Build || order.RuleSerial != 0 || !IsActive(s))
            {
                return false;
            }
            string[] held = s.Weather.SilentNight.HeldOrderIds;
            return held != null && Array.IndexOf(held, order.WorkOrderId) >= 0;
        }

        /// <summary>暂缓施工单的玩家文字（施工队列、悬停、工单面板同一写法）。</summary>
        public static string HoldText(CampaignState s) => GameText.Format("env.silent.hold_reason", CalendarService.ShortDuration(RemainingSeconds(s)));

        // ── 远征准备提示（FGR-ENV-013）──

        /// <summary>按 env.silent.expedition_estimate_hours 估计的远征会不会跨过下一次（或正在进行的）静默夜；会就返回提示文字，否则 null。</summary>
        public static string ExpeditionCrossText(CampaignState s)
        {
            SilentNightState d = s?.Weather?.SilentNight;
            if (d == null || d.NextDay <= 0 || d.StartTick < 0)
            {
                return null;
            }
            long now = GameClock.Ticks;
            long until = now + GameClock.TicksFor(ExpeditionEstimateHours * GameClock.HourSeconds);
            if (!(d.Active || (d.StartTick >= now && d.StartTick <= until)))
            {
                return null;
            }
            double hz = Math.Max(1, GameClock.StepHz);
            if (d.Active)
            {
                // 正在进行：不能再说“会跨过下一个静默夜（X 开始）”——X 已经过去；改说现在出发会怎样、到几点结束。
                return GameText.Format("env.silent.expedition_during", GameClock.FormatDayTime(d.EndTick / hz));
            }
            return GameText.Format("env.silent.expedition_cross", CalendarService.ShortDuration(ExpeditionEstimateHours * GameClock.HourSeconds),
                GameClock.FormatDayTime(d.StartTick / hz));
        }

        // ── 测试捷径（冒烟用：只改日程，效果全部走正式推进）──

        private static bool _skipRaidOnce;

        /// <summary>
        /// 测试捷径（Play 冒烟）：把下一次静默夜排到眼前——<paramref name="warnInSeconds"/> 游戏秒后预警、再 1 游戏秒开始、持续 <paramref name="durationSeconds"/> 游戏秒。
        /// 预警、开始（断链 / 安全模式 / 通知 / 横幅）、结束（风暴残骸 / 总结 / 放行施工）都由 <see cref="WorldStep"/> 照正式流程执行；
        /// <paramref name="raid"/> = false 时这一次不向突袭导演发触发（冒烟后面的步骤不想被突袭打断；突袭触发由 FgSilentNightSelfCheck R1 ⑧断言）。
        /// </summary>
        public static void ScheduleForTests(CampaignState s, double warnInSeconds, double durationSeconds, bool raid)
        {
            SilentNightState d = StateOf(s);
            if (d == null || d.Active)
            {
                return;
            }
            long now = GameClock.Ticks;
            d.NextDay = Math.Max(1, GameClock.DayOf(now / (double)Math.Max(1, GameClock.StepHz)));
            d.WarnTick = now + Math.Max(1, GameClock.TicksFor(warnInSeconds));
            d.StartTick = d.WarnTick + GameClock.TicksFor(1.0);
            d.EndTick = d.StartTick + Math.Max(1, GameClock.TicksFor(durationSeconds));
            d.Warned = false;
            _skipRaidOnce = !raid;
            Revision++;
        }

        // ── 推进 ─────────────────────────────────────────────────────────────────

        /// <summary>世界模拟每步调用（<c>WorldSimulation.StepOnce</c>）：O(1) 比较整数步；预警 / 开始 / 结束那一步做对应的事。只看步序号与存档，与观察无关。</summary>
        public static void WorldStep(CampaignState state, long ticks)
        {
            if (!_installed || state == null)
            {
                return;
            }
            SilentNightState d = StateOf(state);
            if (d.NextDay <= 0)
            {
                ScheduleFirst(state, d, ticks);
            }
            if (d.Active)
            {
                if (ticks >= d.EndTick)
                {
                    End(state, d);
                }
                return;
            }
            if (ticks >= d.EndTick)
            {
                // 时间被跳过（只有自检的跳时与损坏的旧档会发生）：整段错过——不补发效果，排下一次并告警（不静默）。
                SkippedCount++;
                Log.Warning($"[SilentNightService] 第 {d.NextDay} 日的静默夜被跳过（当前步 {ticks} 已过结束步 {d.EndTick}），排下一次。");
                Plan(d, d.NextDay + IntervalDays(state));
                Revision++;
                return;
            }
            if (!d.Warned && ticks >= d.WarnTick)
            {
                Warn(state, d, ticks);
            }
            if (ticks >= d.StartTick)
            {
                Start(state, d);
            }
        }

        private static void ScheduleFirst(CampaignState state, SilentNightState d, long ticks)
        {
            int interval = IntervalDays(state);
            int today = GameClock.DayOf(ticks / (double)Math.Max(1, GameClock.StepHz));
            int day = Math.Max(interval, (today + interval - 1) / interval * interval);
            PlanTicks(day, out long warn, out _, out _);
            if (warn <= ticks)
            {
                day += interval;
            }
            Plan(d, day);
            Revision++;
        }

        private static void Plan(SilentNightState d, int day)
        {
            PlanTicks(day, out long warn, out long start, out long end);
            d.NextDay = day;
            d.WarnTick = warn;
            d.StartTick = start;
            d.EndTick = end;
            d.Warned = false;
        }

        private static Vector3? CoreAt(CampaignState s)
        {
            Vector2 c = SignalUplinkService.CorePosition(s);
            return new Vector3(c.x, 0f, c.y);
        }

        private static void Warn(CampaignState state, SilentNightState d, long ticks)
        {
            d.Warned = true;
            WarnCount++;
            double hz = Math.Max(1, GameClock.StepHz);
            double secs = Math.Max(0, (d.StartTick - ticks) / hz);
            if (ReferenceEquals(state, CampaignSession.Current))
            {
                SignalLinkService.AnnounceSilentNight((float)secs);
            }
            NotificationCenter.Post("silent_night_warning", GameText.Format("env.silent.warn_detail", CalendarService.ShortDuration(secs),
                GameClock.FormatHhMm(d.StartTick / hz), GameClock.FormatHhMm(d.EndTick / hz)), CoreAt(state));
            GuidanceHooks.Raise(GuidanceHooks.EnvFirstSilentWarning);
            Revision++;
            GameEvent.Send(ChangedEvent, false);
        }

        private static void Start(CampaignState state, SilentNightState d)
        {
            if (!d.Warned)
            {
                Warn(state, d, GameClock.Ticks);
            }
            d.Active = true;
            d.Count++;
            StartCount++;
            d.HeldOrderIds = Array.Empty<string>();
            d.ResultSerialAtStart = state.Raids?.Results?.NextSerial ?? 1;
            Revision++; // 先改状态：下面的断链与判定都读“正在静默夜”。
            if (ReferenceEquals(state, CampaignSession.Current))
            {
                SignalLinkService.OnSilentNightStarted();
            }
            // 远征队进入安全模式（继续执行最后的命令；命令由 CanReceiveCommand 拦住）。按编号排序：存档与指纹不依赖遍历顺序。
            var safe = new List<int>(4);
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && !m.IsInFactory && !string.IsNullOrEmpty(m.RegionId) && m.RegionId != HomeValleyLayout.RegionId)
                {
                    safe.Add(m.LogicId);
                }
            }
            safe.Sort();
            foreach (int id in safe)
            {
                SignalLinkService.EnterSafeModeForSilentNight(state, id);
            }
            d.ExpeditionSafeIds = safe.ToArray();
            if (!_skipRaidOnce)
            {
                RaidDirectorService.OnSilentNightStarted(state);
            }
            _skipRaidOnce = false;
            double hz = Math.Max(1, GameClock.StepHz);
            string detail = GameText.Format("env.silent.start_detail", GameClock.FormatHhMm(d.EndTick / hz));
            if (safe.Count > 0)
            {
                detail += GameText.Get("env.silent.end.join") + GameText.Format("env.silent.expedition_safe", safe.Count.ToString(CultureInfo.InvariantCulture));
            }
            NotificationCenter.Post("silent_night", detail, CoreAt(state));
            GuidanceHooks.Raise(GuidanceHooks.EnvFirstSilentNight);
            HomeValleyWorkOrders.MarkAssignmentDirty();
            GameEvent.Send(ChangedEvent, true);
        }

        private static void End(CampaignState state, SilentNightState d)
        {
            d.Active = false;
            EndCount++;
            d.HeldOrderIds = Array.Empty<string>(); // 暂缓的施工单下一次工单推进时回到待分配池，自动开工。
            HomeValleyWorkOrders.MarkAssignmentDirty();
            HomeValleyConstruction.Touch();
            List<(string Item, int Amount)> found = SpawnDebris(state, d, out Vector2? firstAt);
            d.LastSummary = BuildSummary(state, d, found);
            NotificationCenter.Post("silent_night_end", d.LastSummary,
                firstAt.HasValue ? new Vector3(firstAt.Value.x, 0f, firstAt.Value.y) : CoreAt(state));
            Plan(d, d.NextDay + IntervalDays(state));
            Revision++;
            GameEvent.Send(ChangedEvent, false);
        }

        // ── 风暴残骸（FGR-ENV-014）──

        /// <summary>确定性随机（与地图种子、第几次静默夜绑定；不用 System.Random，跨运行时一致）。</summary>
        private struct Rng
        {
            private ulong _s;
            public Rng(int seed, int salt) { _s = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)salt + 0x632BE59BD9B4E019UL); }

            public double Next()
            {
                _s += 0x9E3779B97F4A7C15UL;
                ulong z = _s;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                return (z >> 11) * (1.0 / (1UL << 53));
            }

            public int Range(int min, int maxInclusive) => min + (int)Math.Min(maxInclusive - min, Math.Floor(Next() * (maxInclusive - min + 1)));
        }

        private static readonly List<StormDebris> Normal = new List<StormDebris>(4);
        private static readonly List<StormDebris> Rare = new List<StormDebris>(2);

        private static void LoadDebrisRows()
        {
            Normal.Clear();
            Rare.Clear();
            TbStormDebris table = null;
            try
            {
                table = ConfigSystem.Instance?.Tables?.TbStormDebris;
            }
            catch (Exception e)
            {
                Log.Error("[SilentNightService] 读取 fg.TbStormDebris 失败：" + e.Message);
            }
            if (table == null)
            {
                Log.Warning("[SilentNightService] fg.TbStormDebris 缺失：静默夜结束不放风暴残骸。");
                return;
            }
            var rows = new List<StormDebris>(table.DataList);
            rows.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            foreach (StormDebris r in rows)
            {
                if (r == null || string.IsNullOrEmpty(r.ItemId))
                {
                    continue;
                }
                if (r.Rare == 1)
                {
                    Rare.Add(r);
                }
                else if (r.Weight > 0)
                {
                    Normal.Add(r);
                }
            }
        }

        private static StormDebris PickWeighted(ref Rng rng)
        {
            int total = 0;
            foreach (StormDebris r in Normal)
            {
                total += r.Weight;
            }
            if (total <= 0)
            {
                return null;
            }
            double x = rng.Next() * total;
            foreach (StormDebris r in Normal)
            {
                x -= r.Weight;
                if (x < 0)
                {
                    return r;
                }
            }
            return Normal[Normal.Count - 1];
        }

        /// <summary>找一处放风暴残骸的地面：核心外 ring_min～ring_max 格，生成地形可走、没有建筑 / 传送带占用、已经探索过、附近没有别的地面物（B25：按种子地形找，不写死坐标）。</summary>
        private static bool TryFindDebrisCell(CampaignState s, ref Rng rng, List<Vector2> taken, out Vector2 at)
        {
            at = default;
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            float rMin = Math.Max(1f, Tuning("env.silent.debris_ring_min", 10f));
            float rMax = Math.Max(rMin + 1f, Tuning("env.silent.debris_ring_max", 36f));
            for (int i = 0; i < 32; i++)
            {
                double ang = rng.Next() * Math.PI * 2.0;
                double dist = rMin + rng.Next() * (rMax - rMin);
                var cell = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * dist), core.Y + (int)Math.Round(Math.Sin(ang) * dist));
                if (!WorldGenQuery.IsWalkableGenerated(s, cell.X, cell.Y) || map.OccupantAt(cell) != null || !map.IsExplored(cell))
                {
                    continue;
                }
                var p = new Vector2(cell.X, cell.Y);
                bool near = false;
                foreach (Vector2 t in taken)
                {
                    near |= (t - p).sqrMagnitude < 4f;
                }
                if (s.GroundItems != null)
                {
                    foreach (GroundItemRecord g in s.GroundItems)
                    {
                        near |= g != null && g.RegionId == HomeValleyLayout.RegionId && (g.Position - p).sqrMagnitude < 4f;
                    }
                }
                if (near)
                {
                    continue;
                }
                at = p;
                return true;
            }
            return false;
        }

        private static List<(string Item, int Amount)> SpawnDebris(CampaignState s, SilentNightState d, out Vector2? firstAt)
        {
            firstAt = null;
            var found = new List<(string Item, int Amount)>(4);
            var ids = new List<string>(4);
            LoadDebrisRows();
            var rng = new Rng(s.World?.WorldSeed ?? s.RandomSeed, d.Count);
            int min = Math.Max(0, (int)Math.Round(Tuning("env.silent.debris_min", 2f)));
            int max = Math.Max(min, (int)Math.Round(Tuning("env.silent.debris_max", 4f)));
            int piles = rng.Range(min, max);
            var picks = new List<StormDebris>(piles + 1);
            for (int i = 0; i < piles; i++)
            {
                StormDebris r = PickWeighted(ref rng);
                if (r != null)
                {
                    picks.Add(r);
                }
            }
            if (Rare.Count > 0 && rng.Next() < Math.Max(0f, Math.Min(1f, Tuning("env.silent.debris_rare_chance", 0.25f))))
            {
                picks.Add(Rare[rng.Range(0, Rare.Count - 1)]);
            }
            var taken = new List<Vector2>(picks.Count);
            foreach (StormDebris r in picks)
            {
                int amount = rng.Range(Math.Max(1, r.AmountMin), Math.Max(Math.Max(1, r.AmountMin), r.AmountMax));
                if (!TryFindDebrisCell(s, ref rng, taken, out Vector2 at))
                {
                    continue;
                }
                taken.Add(at);
                string sid = "storm:" + (d.NextDebrisSerial++).ToString(CultureInfo.InvariantCulture);
                GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at, ItemCatalog.ResourceTypeOf(r.ItemId), amount, sid);
                if (g == null)
                {
                    continue;
                }
                ids.Add(g.GroundItemId);
                found.Add((r.ItemId, amount));
                firstAt ??= at;
                // 机器搬回：系统产生的地面物开搬运单进待分配池（与拆除返还、突袭残骸同一套；仓库满时等待，腾出空间自动继续）。
                HomeValleyWorkOrders.TryCreateHaulPool(s, g.GroundItemId);
            }
            d.LastDebrisIds = ids.ToArray();
            if (ids.Count > 0)
            {
                GuidanceHooks.Raise(GuidanceHooks.EnvFirstStormDebris);
            }
            return found;
        }

        // ── 结束总结（FG07 第 4 节“击退了什么、损失了什么、捡到了什么”）──

        private static string BuildSummary(CampaignState s, SilentNightState d, List<(string Item, int Amount)> found)
        {
            int killed = 0, lostB = 0, lostT = 0, lostD = 0, lostM = 0, results = 0;
            bool open = false;
            RaidResultRecord[] all = s.Raids?.Results?.Results;
            if (all != null)
            {
                foreach (RaidResultRecord r in all)
                {
                    if (r == null || r.Serial < d.ResultSerialAtStart)
                    {
                        continue;
                    }
                    results++;
                    killed += r.Killed;
                    lostB += r.LostBuildings;
                    lostT += r.LostTurrets;
                    lostD += r.LostDefenses;
                    lostM += r.LostMachines;
                    open |= r.EndTick < 0;
                }
            }
            string join = GameText.Get("env.silent.end.join");
            var sb = new StringBuilder(128);
            if (results > 0)
            {
                sb.Append(GameText.Format("env.silent.end.raid", killed.ToString(CultureInfo.InvariantCulture)));
                if (open)
                {
                    sb.Append(join).Append(GameText.Get("env.silent.end.raid_pending"));
                }
            }
            else
            {
                bool pending = RaidDirectorService.IncomingWaves(s, GameClock.Ticks).Count > 0;
                sb.Append(GameText.Get(pending ? "env.silent.end.raid_pending" : "env.silent.end.raid_none"));
            }
            sb.Append(join);
            sb.Append(lostB + lostT + lostD + lostM > 0
                ? GameText.Format("env.silent.end.loss", lostB, lostT, lostD, lostM)
                : GameText.Get("env.silent.end.loss_none"));
            sb.Append(join);
            if (found.Count == 0)
            {
                sb.Append(GameText.Get("env.silent.end.debris_none"));
            }
            else
            {
                var items = new StringBuilder(64);
                for (int i = 0; i < found.Count; i++)
                {
                    if (i > 0)
                    {
                        items.Append(GameText.Get("rules.sep_list"));
                    }
                    items.Append(GameText.Format("env.silent.debris_item", ItemCatalog.NameOf(found[i].Item), found[i].Amount.ToString(CultureInfo.InvariantCulture)));
                }
                sb.Append(GameText.Format("env.silent.end.debris", found.Count.ToString(CultureInfo.InvariantCulture), items.ToString()));
            }
            return sb.ToString();
        }

        // ── 日历来源（FGR-ENV-010“日历上始终能看到倒计时”；DEBT-FG7ENV01-01）──

        private sealed class SilentCalendarSource : ICalendarSource
        {
            public CalendarKind Kind => CalendarKind.SilentNight;

            public void Collect(CampaignState state, double fromSeconds, double toSeconds, List<CalendarEntry> into)
            {
                SilentNightState d = state?.Weather?.SilentNight;
                if (d == null || d.NextDay <= 0 || d.StartTick < 0)
                {
                    return;
                }
                double hz = Math.Max(1, GameClock.StepHz);
                int interval = IntervalDays(state);
                // 已排定的那一次按存档里的步；之后的按当前难度与调参推算（最多 14 天视野）。
                for (int k = 0; k < 16; k++)
                {
                    long warn, start, end;
                    int day = d.NextDay + k * interval;
                    if (k == 0)
                    {
                        warn = d.WarnTick;
                        start = d.StartTick;
                        end = d.EndTick;
                    }
                    else
                    {
                        PlanTicks(day, out warn, out start, out end);
                    }
                    double s = start / hz;
                    double e = end / hz;
                    if (s >= toSeconds)
                    {
                        break;
                    }
                    if (e <= fromSeconds)
                    {
                        continue;
                    }
                    into.Add(new CalendarEntry
                    {
                        Kind = CalendarKind.SilentNight,
                        StartSeconds = s,
                        EndSeconds = e,
                        Id = "silent:" + day.ToString(CultureInfo.InvariantCulture),
                        Title = GameText.Get("env.silent.title"),
                        Detail = GameText.Format("env.cal.silent", GameClock.FormatHhMm(s), GameClock.FormatHhMm(e), GameClock.FormatHhMm(warn / hz)),
                    });
                }
            }
        }
    }
}
