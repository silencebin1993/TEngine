using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Logistics;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG7-ENV-03（FG07 FGR-ENV-020 / 021；FG03 FGR-LOG-028 / 045；验收 FGT-ENV-004）：天气。
    /// - 排定（FGR-ENV-020）：按 (世界种子, 序号) 的确定性随机（自带 SplitMix，不用 System.Random；与世界生成 / 掉落 / 突袭分开，FGR-ARC-010），
    ///   提前 env.weather.schedule_days 排好，写进存档（<see cref="WeatherFrontState"/>）。每段晴天之后按幕（fg.TbWeatherAct）决定下一段是不是恶劣天气、哪一种；
    ///   恶劣天气持续 0.5～1.5 日。已排定的不随调参 / 幕变化改变（只影响再往后排的）。天气与静默夜各自排定，可以叠加。
    /// - 预报（FGR-ENV-021）：没有有效的“天气预报”情报（监听站 T2 破译）时，恶劣天气开始前 env.weather.warning_days（0.5 日）预警并出现在日历 / 时间条；
    ///   有有效的天气预报时看得到未来 env.weather.forecast_days（3 日）。
    /// - 效果：酸雨 = 露天、不在净化区的建筑 / 传送带 / 炮塔按游戏分钟批量掉耐久（整数预算，平均精确等于每游戏小时 1%，污染 1 级加倍），归零 = 摧毁留虚影（既有入口）；
    ///   沙暴 = 露天传送带减速（<see cref="BeltNetworkService.SetExposedSlowdown"/>）、太阳能 ×0.4（<see cref="Regions.PowerEnvironment.SandstormProvider"/>）、露天炮塔探测 −20%
    ///   （<see cref="TurretRangeFactor"/>）、画面能见度下降（<see cref="View.WeatherView"/>）；寒潮 = 家园武器散热 +30%（战斗内核 <c>DissipationBonus</c>）、
    ///   管线静止 1 游戏小时结冰（<see cref="PipeNetworkService.SetColdSnap"/> + 内核 <c>FreezeAfterSteps</c>），寒潮结束自动解冻。顶棚下的格子不受酸雨和沙暴影响（<see cref="RoofService"/>）。
    /// - 只在世界模拟步里推进（<see cref="WorldStep"/>：平时 O(1) 比较整数步；酸雨每游戏分钟一次 O(建筑数) 的整数运算；传送带的逐格判定在 AOT 内核
    ///   （<see cref="BeltKernel.CollectAcidHits"/>，Burst，按格错开相位），热更层只处理这一分钟真有整点伤害的格，扣耐久 / 归零摧毁走批量）；
    ///   内核里的派生量（减速、结冰门槛、散热加成、顶棚覆盖）在每步开头 <see cref="PreStep"/> 按存档对齐，读档后第一步就与一直跑的一致（FGR-BASE-021）。
    /// </summary>
    public static class WeatherService
    {
        public const string Clear = "clear";
        public const string AcidRain = "acid_rain";
        public const string Sandstorm = "sandstorm";
        public const string ColdSnap = "cold_snap";

        /// <summary>天气预警 / 开始 / 结束的事件，参数 = 当前天气 ID（空 = 晴）。</summary>
        public const string ChangedEvent = "Weather.Changed";

        private static bool _installed;
        private static readonly WeatherCalendarSource Source = new WeatherCalendarSource();

        /// <summary>预警 / 开始 / 结束 / 排定变化的版本号（HUD、日历、画面据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        public static bool Installed => _installed;

        /// <summary>FG7-ENV-04 的净化区接口：这一格在净化区里（不受酸雨损伤）。由 <see cref="PurificationService.Install"/> 赋值（正在净化的塔的半径之内）；null = 没接（自检复位后）。</summary>
        public static Func<CampaignState, GridCell, bool> PurifiedProvider;

        // ── 统计（自检读）──
        public static int WarnCount { get; private set; }
        public static int StartCount { get; private set; }
        public static int EndCount { get; private set; }
        public static int SkippedCount { get; private set; }
        public static int BatchCount { get; private set; }
        public static int BeltSweepCount { get; private set; }
        public static int BuildingHits { get; private set; }
        public static int BeltHits { get; private set; }
        public static double LastBatchMs { get; private set; }
        /// <summary>中途没有赶上垃圾回收的批次里最慢的一次（这一批自己的耗时）。</summary>
        public static double MaxBatchMs { get; private set; }
        /// <summary>FG8-GEN-02 修复轮（DEBT-FG8EXP02-08）：中途发生了垃圾回收的批次单列——回收是整个进程的停顿，长短取决于整个托管堆
        /// （Editor 全量自检跑到后面堆很大），与这一批做了什么无关；只记次数与其中最慢的一次，不混进 <see cref="MaxBatchMs"/>。</summary>
        public static int GcBatchCount { get; private set; }
        public static double MaxGcBatchMs { get; private set; }
        public static int FrozenNotifyCount { get; private set; }
        /// <summary>FG7-ENV-03 审查修复：内核交回热更层的传送带格累计数（这一分钟有整点伤害的露天格）、一分钟里交回最多的一次、一分钟里摧毁最多的一次。</summary>
        public static long BeltCandidates { get; private set; }
        public static int MaxBeltCandidates { get; private set; }
        public static int MaxBeltsDestroyed { get; private set; }

        /// <summary>自检：清零“最慢一次 / 最多一次”的统计（分段量性能）。</summary>
        public static void ResetBatchStats()
        {
            LastBatchMs = MaxBatchMs = MaxGcBatchMs = 0;
            MaxBeltCandidates = MaxBeltsDestroyed = GcBatchCount = 0;
        }

        /// <summary>正式入口（<c>GameRoot.Startup</c>）：接上太阳能的沙暴系数、日历来源。幂等。</summary>
        public static void Install()
        {
            _installed = true;
            PowerEnvironment.SandstormProvider = IsSandstormNow;
            CalendarService.Register(Source);
            Revision++;
        }

        /// <summary>自检之间复位（断开接入点，不动存档）。</summary>
        public static void ResetForTests()
        {
            _installed = false;
            if (PowerEnvironment.SandstormProvider == (Func<CampaignState, bool>)IsSandstormNow)
            {
                PowerEnvironment.SandstormProvider = null;
            }
            CalendarService.Unregister(Source);
            WarnCount = StartCount = EndCount = SkippedCount = BatchCount = BeltSweepCount = BuildingHits = BeltHits = FrozenNotifyCount = 0;
            BeltCandidates = 0;
            ResetBatchStats();
            PhaseCache.Clear();
            _beltKernel = null;
            _pipeKernel = null;
            _warnLeadTicks = -1;
            Revision++;
        }

        // ── 调参（fg.TbHomeTuning env.weather.*）──
        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static double DurationMinDays => Math.Max(0.05, Tuning("env.weather.duration_min_days", 0.5f));
        public static double DurationMaxDays => Math.Max(DurationMinDays, Tuning("env.weather.duration_max_days", 1.5f));
        public static double FirstDay => Math.Max(1.0, Tuning("env.weather.first_day", 3f));
        public static double WarningDays => Math.Max(0.0, Tuning("env.weather.warning_days", 0.5f));
        public static double ForecastDays => Math.Max(WarningDays, Tuning("env.weather.forecast_days", 3f));
        public static double ScheduleDays => Math.Max(ForecastDays + 0.5, Tuning("env.weather.schedule_days", 4f));
        public static double AcidPctPerHour => Math.Max(0.0, Tuning("env.weather.acid_pct_per_hour", 1f));
        public static double DamageNotifyHours => Math.Max(0.05, Tuning("env.weather.damage_notify_hours", 1f));
        public static float SandDetectFactor => Mathf.Clamp(Tuning("env.weather.sand_detect_factor", 0.8f), 0.1f, 1f);
        public static float ColdDissipationMult => Mathf.Max(1f, Tuning("env.weather.cold_dissipation_mult", 1.3f));
        public static int PollutionDoubleLevel => Math.Max(1, GridContent.TuningInt("grid.pollution_warn_level"));
        public static double FreezeIdleHours => Math.Max(0.01, Tuning("logistics.pipe.freeze_idle_hours", 1f));

        // ── 表（fg.TbWeather / fg.TbWeatherAct）──
        private static Tables T => ConfigSystem.Instance?.Tables;

        public static Weather KindRow(string id)
        {
            try
            {
                return string.IsNullOrEmpty(id) ? T?.TbWeather?.GetOrDefault(Clear) : T?.TbWeather?.GetOrDefault(id);
            }
            catch (Exception e)
            {
                Log.Error("[WeatherService] 读取 fg.TbWeather 失败：" + e.Message);
                return null;
            }
        }

        public static string NameOf(string id) => GameText.Get(KindRow(id)?.NameKey ?? "env.weather.name.clear");
        public static string GlyphOf(string id) => KindRow(id)?.Glyph ?? "☀";

        /// <summary>天气效果的玩家文字（数值来自调参，单一真相）。</summary>
        public static string EffectText(string id)
        {
            switch (id)
            {
                case AcidRain:
                    return GameText.Format("env.weather.effect.acid_rain", Pct(AcidPctPerHour));
                case Sandstorm:
                    return GameText.Format("env.weather.effect.sandstorm", Pct((1f - SandDetectFactor) * 100.0), BeltNetworkService.SandstormSlowPercent,
                        Pct((1f - PowerEnvironment.SandstormFactor) * 100.0));
                case ColdSnap:
                    return GameText.Format("env.weather.effect.cold_snap", Pct((ColdDissipationMult - 1f) * 100.0), FreezeIdleHours.ToString("0.##", CultureInfo.InvariantCulture));
                default:
                    return GameText.Get("env.weather.effect.clear");
            }
        }

        private static string Pct(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>当前幕的频率行（超过表里最大一幕按最大一幕；表缺失用内置初值）。</summary>
        public static void ActRow(CampaignState s, out float badChance, out double clearMin, out double clearMax, out int wa, out int ws, out int wc)
        {
            int act = Math.Max(1, s?.Progress?.Act ?? 1);
            WeatherAct best = null;
            try
            {
                IReadOnlyList<WeatherAct> rows = T?.TbWeatherAct?.DataList;
                if (rows != null)
                {
                    foreach (WeatherAct r in rows)
                    {
                        if (r != null && r.Act <= act && (best == null || r.Act > best.Act))
                        {
                            best = r;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[WeatherService] 读取 fg.TbWeatherAct 失败：" + e.Message);
            }
            badChance = best != null ? Mathf.Clamp01(best.BadChance) : 0.5f;
            clearMin = best != null ? Math.Max(0.05, best.ClearMinDays) : 1.0;
            clearMax = best != null ? Math.Max(clearMin, best.ClearMaxDays) : 2.5;
            wa = best?.WAcid ?? 4;
            ws = best?.WSand ?? 3;
            wc = best?.WCold ?? 3;
        }

        // ── 查询 ─────────────────────────────────────────────────────────────────

        public static WeatherFrontState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            s.Weather ??= new WeatherState();
            return s.Weather.Fronts ??= new WeatherFrontState();
        }

        /// <summary>当前天气 ID（晴 = <see cref="Clear"/>）。</summary>
        public static string ActiveKind(CampaignState s)
        {
            string k = s?.Weather?.Fronts?.ActiveKind;
            return string.IsNullOrEmpty(k) ? Clear : k;
        }

        public static bool IsActive(CampaignState s, string kind) => ActiveKind(s) == kind;
        public static bool IsSandstormNow(CampaignState s) => IsActive(s, Sandstorm);

        /// <summary>正在进行的那一段（晴 = null）。</summary>
        public static WeatherFrontRecord ActiveFront(CampaignState s)
        {
            WeatherFrontState d = s?.Weather?.Fronts;
            if (d == null || d.ActiveSerial == 0)
            {
                return null;
            }
            foreach (WeatherFrontRecord f in d.Fronts ?? Array.Empty<WeatherFrontRecord>())
            {
                if (f != null && f.Serial == d.ActiveSerial)
                {
                    return f;
                }
            }
            return null;
        }

        /// <summary>这一格此刻受不受酸雨 / 沙暴影响（露天 = 不在顶棚下）。</summary>
        public static bool Exposed(CampaignState s, GridCell cell) => !RoofService.IsCovered(s, cell);

        /// <summary>沙暴时露天炮塔的探测（开火）距离系数；顶棚下的炮塔不受影响（整座都在顶棚下）。</summary>
        public static float TurretRangeFactor(CampaignState s, BuildingRecord b)
        {
            if (s == null || b == null || !IsSandstormNow(s) || RoofService.IsBuildingCovered(s, b))
            {
                return 1f;
            }
            return SandDetectFactor;
        }

        /// <summary>寒潮时家园武器的散热加成（0 = 正常）。</summary>
        public static float DissipationBonus(CampaignState s) => IsActive(s, ColdSnap) ? ColdDissipationMult - 1f : 0f;

        /// <summary>沙暴时露天传送带的减速百分比（0 = 正常）。</summary>
        public static int BeltSlowPercent(CampaignState s) => IsSandstormNow(s) ? BeltNetworkService.SandstormSlowPercent : 0;

        /// <summary>寒潮时网络静止多少内核步结冰（0 = 不结冰）。</summary>
        public static long FreezeSteps(CampaignState s, PipeKernel k)
        {
            if (k == null || s?.Pipes == null || !s.Pipes.ColdSnap)
            {
                return 0;
            }
            return Math.Max(1L, (long)Math.Round(FreezeIdleHours * PipeNetworkService.GameHourSeconds * k.Config.StepHz));
        }

        // ── 预报（FGR-ENV-021）──

        /// <summary>有没有有效的天气预报情报（监听站 T2 破译）；有时给出剩余步数。</summary>
        public static bool HasForecast(CampaignState s, long now, out long ticksLeft) =>
            IntelService.HasValid(s, IntelCatalog.KindWeather, null, now, out ticksLeft);

        /// <summary>现在看得到多远的天气（游戏秒）：有天气预报 = 3 日，否则 = 0.5 日预警。</summary>
        public static double HorizonSeconds(CampaignState s, long now) => (HasForecast(s, now, out _) ? ForecastDays : WarningDays) * GameClock.DaySeconds;

        /// <summary>日历页脚 / 情报面板的一句：现在的天气预报看得到多远、怎么看得更远。</summary>
        public static string HorizonNote(CampaignState s)
        {
            long now = GameClock.Ticks;
            return HasForecast(s, now, out long left)
                ? GameText.Format("env.weather.horizon_long", ForecastDays.ToString("0.#", CultureInfo.InvariantCulture), CalendarService.ShortDuration(left / (double)Math.Max(1, GameClock.StepHz)))
                : GameText.Format("env.weather.horizon_short", CalendarService.ShortDuration(WarningDays * GameClock.DaySeconds), ForecastDays.ToString("0.#", CultureInfo.InvariantCulture));
        }

        /// <summary>世界时间条的天气一段：“☂ 酸雨” / “☀ 晴”，后面接看得到的下一段（“≋ 沙暴（3时后）”）。</summary>
        public static string HudText(CampaignState s, double nowSeconds)
        {
            // 数据走日历来源（与日历时间轴同一口径；来源只给看得到的段：没有天气预报时只有 0.5 日内的）。
            var sb = new StringBuilder();
            string current = null;
            string next = null;
            double nextIn = 0;
            foreach (CalendarEntry e in CalendarService.Collect(s, nowSeconds - 1e-6, nowSeconds + ForecastDays * GameClock.DaySeconds))
            {
                if (e.Kind != CalendarKind.Weather)
                {
                    continue;
                }
                if (e.StartSeconds <= nowSeconds && e.EndSeconds > nowSeconds)
                {
                    current ??= e.Title;
                }
                else if (e.StartSeconds > nowSeconds && next == null)
                {
                    next = e.Title;
                    nextIn = e.StartSeconds - nowSeconds;
                }
            }
            sb.Append(current == null ? GameText.Get("env.hud.weather_clear") : GameText.Format("env.hud.weather_now", current));
            if (next != null)
            {
                sb.Append("  ").Append(GameText.Format("env.hud.weather_next", next, CalendarService.ShortDuration(nextIn)));
            }
            return sb.ToString();
        }

        /// <summary>天气预报情报的内容：从 <paramref name="fromTick"/> 起 env.weather.forecast_days 日内排定的恶劣天气（同一份排定，确定性）。</summary>
        public static string IntelSummary(CampaignState s, long fromTick)
        {
            WeatherFrontState d = s?.Weather?.Fronts;
            long until = fromTick + GameClock.TicksFor(ForecastDays * GameClock.DaySeconds);
            var parts = new List<string>(4);
            double hz = Math.Max(1, GameClock.StepHz);
            foreach (WeatherFrontRecord f in d?.Fronts ?? Array.Empty<WeatherFrontRecord>())
            {
                if (f == null || f.EndTick <= fromTick || f.StartTick >= until)
                {
                    continue;
                }
                parts.Add(GameText.Format("env.weather.intel_item", GlyphOf(f.Kind), GameClock.FormatDayTime(f.StartTick / hz), NameOf(f.Kind),
                    CalendarService.ShortDuration((f.EndTick - f.StartTick) / hz)));
            }
            return parts.Count == 0
                ? GameText.Format("env.weather.intel_clear", ForecastDays.ToString("0.#", CultureInfo.InvariantCulture))
                : string.Join(GameText.Get("env.weather.intel_join"), parts);
        }

        // ── 测试捷径（冒烟 / 自检：只改日程，效果全部走正式推进）──

        /// <summary>
        /// 测试捷径：把一段 <paramref name="kind"/> 排到眼前——<paramref name="startInSeconds"/> 游戏秒后开始、持续 <paramref name="durationSeconds"/> 游戏秒；
        /// 与它重叠或更早的、还没开始的排定让开（挪走）。预警、开始、效果、结束都由 <see cref="WorldStep"/> 照正式流程执行。正在进行恶劣天气时不排（返回 false）。
        /// </summary>
        public static bool ScheduleForTests(CampaignState s, string kind, double startInSeconds, double durationSeconds)
        {
            WeatherFrontState d = StateOf(s);
            if (d == null || d.ActiveSerial != 0 || KindRow(kind) == null || kind == Clear)
            {
                return false;
            }
            long now = GameClock.Ticks;
            EnsurePlanned(s, d, now);
            long start = now + Math.Max(1, GameClock.TicksFor(startInSeconds));
            long end = start + Math.Max(1, GameClock.TicksFor(durationSeconds));
            var list = new List<WeatherFrontRecord>(d.Fronts.Length + 1);
            foreach (WeatherFrontRecord f in d.Fronts)
            {
                if (f != null && f.StartTick > end)
                {
                    list.Add(f);
                }
            }
            list.Insert(0, new WeatherFrontRecord { Serial = d.NextSerial++, Kind = kind, StartTick = start, EndTick = end, Warned = false });
            d.Fronts = list.ToArray();
            d.PlannedUntilTick = Math.Max(d.PlannedUntilTick, end);
            Revision++;
            return true;
        }

        /// <summary>自检：把排定一直排到 <paramref name="days"/> 个游戏日之后（与正式推进同一个排定函数，只是视野更远），返回排好的恶劣天气段数。</summary>
        public static int PlanAheadForTests(CampaignState s, double days)
        {
            WeatherFrontState d = StateOf(s);
            if (d == null)
            {
                return 0;
            }
            EnsurePlanned(s, d, GameClock.Ticks, days);
            return d.Fronts.Length;
        }

        // ── 每步开头：内核派生量对齐（读档 / 内核重建后第一步就一致）──

        private static BeltKernel _beltKernel;
        private static int _beltSlow = -1;
        private static PipeKernel _pipeKernel;
        private static long _pipeFreeze = -1;

        /// <summary>
        /// 世界模拟每步开头（地点内核步进之前，<c>WorldSimulation.StepOnce</c>）调用：把天气对传送带 / 管线 / 战斗内核的派生量与顶棚覆盖按存档对齐。
        /// 平时 O(1)（比较缓存）；内核换了实例（读档 / 进出家园）时重新写一次，顶棚覆盖 O(顶棚块数 × 16)。
        /// </summary>
        public static void PreStep(CampaignState state)
        {
            if (!_installed || state == null || !ReferenceEquals(state, BeltNetworkService.BoundState ?? state))
            {
                return;
            }
            if (BeltNetworkService.IsRunning)
            {
                BeltKernel bk = BeltNetworkService.Kernel;
                int want = BeltSlowPercent(state);
                if (!ReferenceEquals(bk, _beltKernel) || want != _beltSlow)
                {
                    BeltNetworkService.SetExposedSlowdown(want);
                    _beltKernel = bk;
                    _beltSlow = want;
                }
            }
            if (PipeNetworkService.IsRunning)
            {
                PipeKernel pk = PipeNetworkService.Kernel;
                long want = FreezeSteps(state, pk);
                if (!ReferenceEquals(pk, _pipeKernel) || want != _pipeFreeze)
                {
                    pk.SetFreezeAfterSteps(want);
                    _pipeKernel = pk;
                    _pipeFreeze = want;
                }
            }
            CombatSite home = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            // 经桥接层门面写散热加成（FG14 §5 硬约束 3：Campaign/Combat/ 以外不直接碰战斗内核）；门面内与当前值一致时不写，O(1)。
            home?.SyncDissipationBonus(DissipationBonus(state));
            RoofService.SyncBeltCover(state);
        }

        // ── 推进 ─────────────────────────────────────────────────────────────────

        /// <summary>世界模拟每步调用（静默夜之后）：排定、预警 / 开始 / 结束、酸雨按游戏分钟结算、寒潮结冰告警。只看步序号与存档，与观察无关。</summary>
        public static void WorldStep(CampaignState state, long ticks)
        {
            if (!_installed || state == null)
            {
                return;
            }
            WeatherFrontState d = StateOf(state);
            int hz = Math.Max(1, GameClock.StepHz);
            bool second = ticks % hz == 0;
            if (second || d.PlannedUntilTick < 0 || _warnLeadTicks < 0)
            {
                // 排定视野按天计，每游戏秒对一次就够（只看步序号，确定性）；预警提前量随调参，每游戏秒重算一次。
                EnsurePlanned(state, d, ticks);
                _warnLeadTicks = GameClock.TicksFor(WarningDays * GameClock.DaySeconds);
            }
            WeatherFrontRecord active = d.ActiveSerial != 0 ? ActiveFront(state) : null;
            if (d.ActiveSerial != 0 && active == null)
            {
                // 存档损坏（进行中的那一段找不到）：按晴收尾，不静默。
                Log.Warning("[WeatherService] 进行中的天气记录丢失，按天气结束处理。");
                End(state, d, null);
            }
            if (active != null)
            {
                if (active.Kind == AcidRain)
                {
                    long minute = MinuteTicks;
                    int guard = 0;
                    while (d.NextBatchTick >= 0 && ticks >= d.NextBatchTick && guard++ < 4)
                    {
                        d.AcidMinutes++;
                        AcidBatch(state, d, d.AcidMinutes);
                        d.NextBatchTick += minute;
                    }
                }
                else if (active.Kind == ColdSnap && second)
                {
                    CheckFrozen(state, d, ticks);
                }
                if (ticks >= active.EndTick)
                {
                    End(state, d, active);
                }
                return;
            }
            WeatherFrontRecord next = d.Fronts.Length > 0 ? d.Fronts[0] : null;
            if (next == null)
            {
                return;
            }
            if (ticks >= next.EndTick)
            {
                // 时间被整段跳过（只有自检跳时 / 损坏的旧档）：不补发效果，告警并移走。
                SkippedCount++;
                Log.Warning($"[WeatherService] 第 {next.Serial} 段天气（{next.Kind}）被跳过（当前步 {ticks} 已过结束步 {next.EndTick}）。");
                RemoveFront(d, next);
                Revision++;
                return;
            }
            if (!next.Warned && ticks >= next.StartTick - _warnLeadTicks)
            {
                Warn(state, next, ticks);
            }
            if (ticks >= next.StartTick)
            {
                Start(state, d, next, ticks);
            }
        }

        private static long _warnLeadTicks = -1;

        private static long MinuteTicks => Math.Max(1L, GameClock.TicksFor(GameClock.HourSeconds / 60.0));

        /// <summary>排定到 now + env.weather.schedule_days（旧档 / 新档第一次：从 max(现在, 第 env.weather.first_day 日) 起排，不追溯）。O(新排的段数)，平时 O(1)。</summary>
        private static void EnsurePlanned(CampaignState s, WeatherFrontState d, long now, double days = -1)
        {
            long horizon = now + GameClock.TicksFor((days > 0 ? days : ScheduleDays) * GameClock.DaySeconds);
            if (d.PlannedUntilTick >= horizon)
            {
                return;
            }
            if (d.PlannedUntilTick < 0)
            {
                d.PlannedUntilTick = Math.Max(now, GameClock.TicksFor((FirstDay - 1.0) * GameClock.DaySeconds));
            }
            int seed = s.World?.WorldSeed ?? s.RandomSeed;
            ActRow(s, out float bad, out double clearMin, out double clearMax, out int wa, out int ws, out int wc);
            var list = new List<WeatherFrontRecord>(d.Fronts ?? Array.Empty<WeatherFrontRecord>());
            int guard = 0;
            double day = GameClock.DaySeconds;
            while (d.PlannedUntilTick < horizon && guard++ < 64)
            {
                var rng = new Rng(seed, d.NextSerial);
                int serial = d.NextSerial++;
                double gap = clearMin + (clearMax - clearMin) * rng.Next();
                long start = d.PlannedUntilTick + Math.Max(1, GameClock.TicksFor(gap * day));
                if (rng.Next() < bad && wa + ws + wc > 0)
                {
                    double pick = rng.Next() * (wa + ws + wc);
                    string kind = pick < wa ? AcidRain : pick < wa + ws ? Sandstorm : ColdSnap;
                    double dur = DurationMinDays + (DurationMaxDays - DurationMinDays) * rng.Next();
                    long end = start + Math.Max(1, GameClock.TicksFor(dur * day));
                    list.Add(new WeatherFrontRecord { Serial = serial, Kind = kind, StartTick = start, EndTick = end });
                    d.PlannedUntilTick = end;
                }
                else
                {
                    d.PlannedUntilTick = start;
                }
            }
            d.Fronts = list.ToArray();
            Revision++;
        }

        private static void RemoveFront(WeatherFrontState d, WeatherFrontRecord f)
        {
            var list = new List<WeatherFrontRecord>(d.Fronts.Length);
            foreach (WeatherFrontRecord x in d.Fronts)
            {
                if (x != null && !ReferenceEquals(x, f))
                {
                    list.Add(x);
                }
            }
            d.Fronts = list.ToArray();
        }

        private static Vector3? CoreAt(CampaignState s)
        {
            Vector2 c = SignalUplinkService.CorePosition(s);
            return new Vector3(c.x, 0f, c.y);
        }

        private static void Warn(CampaignState state, WeatherFrontRecord f, long ticks)
        {
            f.Warned = true;
            WarnCount++;
            double hz = Math.Max(1, GameClock.StepHz);
            NotificationCenter.Post("weather_warning", GameText.Format("env.weather.warn_detail", GlyphOf(f.Kind) + " " + NameOf(f.Kind),
                CalendarService.ShortDuration(Math.Max(0, (f.StartTick - ticks) / hz)), GameClock.FormatDayTime(f.StartTick / hz), GameClock.FormatDayTime(f.EndTick / hz),
                EffectText(f.Kind)), CoreAt(state));
            GuidanceHooks.Raise(GuidanceHooks.EnvFirstWeatherWarning);
            Revision++;
            GameEvent.Send(ChangedEvent, ActiveKind(state));
        }

        private static void Start(CampaignState state, WeatherFrontState d, WeatherFrontRecord f, long ticks)
        {
            if (!f.Warned)
            {
                Warn(state, f, ticks);
            }
            d.ActiveSerial = f.Serial;
            d.ActiveKind = f.Kind;
            d.Count++;
            d.AcidMinutes = 0;
            d.NextBatchTick = f.Kind == AcidRain ? ticks + MinuteTicks : -1;
            d.DamagedBuildings = d.DamagedBelts = d.Destroyed = d.FrozenPeak = 0;
            d.HitBuildings = Array.Empty<string>();
            d.HitBelts = Array.Empty<long>();
            d.LastDamageNotifyTick = -1;
            StartCount++;
            if (f.Kind == ColdSnap)
            {
                PipeNetworkService.SetColdSnap(state, true);
            }
            double hz = Math.Max(1, GameClock.StepHz);
            NotificationCenter.Post("weather", GameText.Format("env.weather.start_detail", GlyphOf(f.Kind) + " " + NameOf(f.Kind), GameClock.FormatDayTime(f.EndTick / hz),
                EffectText(f.Kind)), CoreAt(state));
            GuidanceHooks.Raise(GuidanceHooks.EnvFirstWeather);
            Progression.MechanicCodex.OnWeatherStarted(f.Kind); // FG9-UX-01：第一次经历这种天气解锁事件条目
            Revision++;
            GameEvent.Send(ChangedEvent, f.Kind);
        }

        private static void End(CampaignState state, WeatherFrontState d, WeatherFrontRecord f)
        {
            string kind = f?.Kind ?? d.ActiveKind;
            d.ActiveSerial = 0;
            d.ActiveKind = string.Empty;
            d.NextBatchTick = -1;
            EndCount++;
            if (kind == ColdSnap || (state.Pipes != null && state.Pipes.ColdSnap))
            {
                PipeNetworkService.SetColdSnap(state, false); // 寒潮结束自动解冻（下一步内核的结冰门槛归零）
            }
            string summary = kind == AcidRain && (d.DamagedBuildings + d.DamagedBelts + d.Destroyed) > 0
                ? GameText.Format("env.weather.end.acid", d.DamagedBuildings, d.DamagedBelts, d.Destroyed)
                : kind == ColdSnap && d.FrozenPeak > 0
                    ? GameText.Format("env.weather.end.frozen", d.FrozenPeak)
                    : GameText.Get("env.weather.end.none");
            NotificationCenter.Post("weather_end", GameText.Format("env.weather.end_detail", GlyphOf(kind) + " " + NameOf(kind), summary), CoreAt(state));
            if (f != null)
            {
                RemoveFront(d, f);
            }
            Revision++;
            GameEvent.Send(ChangedEvent, Clear);
        }

        // ── 酸雨：按游戏分钟批量结算（FG07 第 7 节）──

        private static readonly List<int3> BeltCells = new List<int3>(256);
        private static readonly List<GridCell> BatchCells = new List<GridCell>(256);
        private static readonly List<int> BatchAmounts = new List<int>(256);
        private static readonly List<GridCell> BatchDestroyed = new List<GridCell>(8);
        private static readonly HashSet<string> DamagedIds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<long> DamagedBeltKeys = new HashSet<long>();
        private static readonly List<string> NewHitIds = new List<string>(16);
        private static readonly List<long> NewHitBelts = new List<long>(64);
        private static readonly Dictionary<string, int> PhaseCache = new Dictionary<string, int>(StringComparer.Ordinal);
        private static WeatherFrontState _hitFor;
        private static string[] _hitIdsRef;
        private static long[] _hitBeltsRef;

        /// <summary>这一段酸雨里受损过的建筑 / 传送带格的去重集合以存档为准（读档后接着数，与一直跑的计数一致）。</summary>
        private static void EnsureHitSets(WeatherFrontState d)
        {
            if (ReferenceEquals(_hitFor, d) && ReferenceEquals(_hitIdsRef, d.HitBuildings) && ReferenceEquals(_hitBeltsRef, d.HitBelts))
            {
                return;
            }
            DamagedIds.Clear();
            DamagedBeltKeys.Clear();
            foreach (string id in d.HitBuildings ?? Array.Empty<string>())
            {
                DamagedIds.Add(id);
            }
            foreach (long key in d.HitBelts ?? Array.Empty<long>())
            {
                DamagedBeltKeys.Add(key);
            }
            _hitFor = d;
            _hitIdsRef = d.HitBuildings;
            _hitBeltsRef = d.HitBelts;
        }

        private static T[] Append<T>(T[] old, List<T> more)
        {
            old = old ?? Array.Empty<T>();
            var next = new T[old.Length + more.Count];
            Array.Copy(old, next, old.Length);
            more.CopyTo(next, old.Length);
            return next;
        }

        /// <summary>倍率 1 时每分钟掉最大耐久的多少点（浮点）：最大耐久 × 每小时百分比 × 系数 / 6000。
        /// FG7-ENV-04：<paramref name="factor"/> = 归还面积里程碑的全局天气损伤系数（5,000 格 = 0.75，<see cref="PurificationService.WeatherDamageFactor"/>）。</summary>
        public static double PerMinute(double maxHp, double factor = 1.0) => maxHp * AcidPctPerHour * Math.Max(0.0, factor) / 6000.0;

        /// <summary>第 k 个游戏分钟这一类目标该掉多少点（整数预算：前 k 分钟累计 = floor(最大耐久 × 每小时百分比 × 倍率 × 系数 × k / 6000)，平均精确、存读档一致）。
        /// 与传送带内核的逐格结算同一个函数（<see cref="BeltAcid.MinuteDamage"/>）。</summary>
        public static int MinuteDamage(double maxHp, int mult, long k, double factor = 1.0) => BeltAcid.MinuteDamage(PerMinute(maxHp, factor) * mult, k);

        /// <summary>按建筑 ID 的确定性相位（0～59 个游戏分钟；FNV-1a，跨运行时一致）。按 ID 缓存（每分钟不重算字符串哈希）。</summary>
        private static int Phase(string id)
        {
            id = id ?? string.Empty;
            if (PhaseCache.TryGetValue(id, out int p))
            {
                return p;
            }
            uint h = 2166136261;
            foreach (char ch in id)
            {
                h = (h ^ ch) * 16777619;
            }
            p = (int)(h % 60);
            PhaseCache[id] = p;
            return p;
        }

        private static int Mult(CampaignState s, GridCell cell)
        {
            HomeGridMap.Chunk chunk = HomeGridService.MapFor(s).ChunkAt(cell, out int idx);
            return chunk != null && chunk.Pollution[idx] >= PollutionDoubleLevel ? 2 : 1;
        }

        private static bool Purified(CampaignState s, GridCell cell) => PurifiedProvider != null && PurifiedProvider(s, cell);

        private static void AcidBatch(CampaignState state, WeatherFrontState d, long k)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int gc0 = System.GC.CollectionCount(0);
            BatchCount++;
            EnsureHitSets(d);
            NewHitIds.Clear();
            NewHitBelts.Clear();
            int buildings = 0;
            int belts = 0;
            float minPct = 1f;
            // FG7-ENV-04：归还面积里程碑的全局天气损伤系数（这一分钟算一次）。
            double factor = PurificationService.WeatherDamageFactor(state);
            // 建筑与炮塔（核心不算：归还核心不会被天气打坏；虚影 / 被摧毁 / 搬迁目标不扣）。复制一份列表：摧毁会改建筑表。
            BuildingRecord[] records = state.BuildingRecords ?? Array.Empty<BuildingRecord>();
            foreach (BuildingRecord b in records)
            {
                if (b == null || b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore || HomeGridService.IsRelocationGhost(b)
                    || (b.ConstructionState != BuildingConstructionState.Operational && b.ConstructionState != BuildingConstructionState.Disabled))
                {
                    continue;
                }
                var pivot = new GridCell(b.GridX, b.GridY);
                float bmax = BuildingOps.MaxDurability(b.BuildingTypeId);
                // 每座建筑按 ID 错开相位：整点伤害分散到一小时的各个分钟（平均仍精确每游戏小时 1%；确定性，与存读档无关）。
                long kb = k + Phase(b.BuildingId);
                if (MinuteDamage(bmax, 2, kb, factor) <= 0)
                {
                    continue; // 这一分钟连加倍的都没有整点伤害：不查地格（平时大多数分钟都在这里跳过）
                }
                // FG7-ENV-04（FGR-ENV-043）：被再污染、处于“受污染”状态的建筑天气损伤加倍（直到所在格子重新净化到 0 级）。
                int mult = PurificationService.IsContaminated(state, b.BuildingId) ? 2 : Mult(state, pivot);
                int amount = MinuteDamage(bmax, mult, kb, factor);
                if (amount <= 0 || RoofService.IsBuildingCovered(state, b) || Purified(state, pivot))
                {
                    continue;
                }
                string id = b.BuildingId;
                bool destroyed = BuildingOps.ApplyDamage(state, id, amount, notify: false);
                BuildingHits++;
                buildings++;
                if (DamagedIds.Add(id))
                {
                    d.DamagedBuildings++;
                    NewHitIds.Add(id);
                }
                if (destroyed)
                {
                    d.Destroyed++;
                }
                else
                {
                    minPct = Mathf.Min(minPct, b.Health / Mathf.Max(1f, BuildingOps.MaxDurability(b.BuildingTypeId)));
                }
            }
            // 传送带（FG7-ENV-03 审查修复，P1）：逐格判定在 AOT 内核（Burst：露天、地面、这一分钟按格错开相位后真有整点伤害的格才交回来）；
            // 热更层只对交回的格查污染倍率与净化区（每分钟约 格数 × 每分钟伤害率），扣耐久、归零摧毁都走批量（受损表 / 虚影 / 通知各一次）。
            if (BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, state))
            {
                BeltSweepCount++;
                BeltNetworkService.Kernel.CollectAcidHits(k, PerMinute(BeltNetworkService.MaxHp(0), factor), PerMinute(BeltNetworkService.MaxHp(1), factor),
                    PerMinute(BeltNetworkService.MaxHp(2), factor), BeltCells);
                BeltCandidates += BeltCells.Count;
                MaxBeltCandidates = Math.Max(MaxBeltCandidates, BeltCells.Count);
                BatchCells.Clear();
                BatchAmounts.Clear();
                foreach (int3 c in BeltCells)
                {
                    var cell = new GridCell(c.x, c.y);
                    int a1 = c.z & 0x3FF;
                    int a2 = (c.z >> 10) & 0x3FF;
                    int amount = a1 == a2 ? a1 : Mult(state, cell) == 2 ? a2 : a1;
                    if (amount <= 0 || Purified(state, cell))
                    {
                        continue;
                    }
                    BatchCells.Add(cell);
                    BatchAmounts.Add(amount);
                    long key = ((long)c.x << 32) ^ (uint)c.y;
                    if (DamagedBeltKeys.Add(key))
                    {
                        d.DamagedBelts++;
                        NewHitBelts.Add(key);
                    }
                }
                if (BatchCells.Count > 0)
                {
                    BatchDestroyed.Clear();
                    int hits = BeltNetworkService.TryDamageBatch(state, BatchCells, BatchAmounts, BatchDestroyed);
                    BeltHits += hits;
                    belts += hits;
                    d.Destroyed += BatchDestroyed.Count;
                    MaxBeltsDestroyed = Math.Max(MaxBeltsDestroyed, BatchDestroyed.Count);
                }
            }
            // 去重集合按“第一次受损”的顺序追加（确定性；不再每分钟整表排序重建，O(新受损数)）。
            if (NewHitIds.Count > 0)
            {
                d.HitBuildings = Append(d.HitBuildings, NewHitIds);
                _hitIdsRef = d.HitBuildings;
            }
            if (NewHitBelts.Count > 0)
            {
                d.HitBelts = Append(d.HitBelts, NewHitBelts);
                _hitBeltsRef = d.HitBelts;
            }
            if ((buildings + belts) > 0 && (d.LastDamageNotifyTick < 0 || GameClock.Ticks - d.LastDamageNotifyTick >= GameClock.TicksFor(DamageNotifyHours * GameClock.HourSeconds)))
            {
                d.LastDamageNotifyTick = GameClock.Ticks;
                NotificationCenter.Post("weather_damage", GameText.Format("env.weather.damage_detail", d.DamagedBuildings, d.DamagedBelts, Mathf.RoundToInt(minPct * 100f)), CoreAt(state));
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastBatchMs = ms;
            if (System.GC.CollectionCount(0) != gc0)
            {
                GcBatchCount++;
                MaxGcBatchMs = Math.Max(MaxGcBatchMs, ms);
            }
            else
            {
                MaxBatchMs = Math.Max(MaxBatchMs, ms);
            }
        }

        // ── 寒潮：结冰告警（FGR-LOG-045“结冰时有视觉效果和警告”）──

        private static void CheckFrozen(CampaignState state, WeatherFrontState d, long ticks)
        {
            if (!PipeNetworkService.IsRunning || !ReferenceEquals(PipeNetworkService.BoundState, state))
            {
                return;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            int frozen = k.FrozenNetworks;
            if (frozen <= d.FrozenPeak)
            {
                return;
            }
            d.FrozenPeak = frozen;
            int nets = k.NetworkCount;
            for (int n = 0; n < nets; n++)
            {
                if (k.TryGetNetworkInfo(n, out PipeNetInfo info) && info.Frozen)
                {
                    FrozenNotifyCount++;
                    NotificationCenter.Post("pipe_frozen", GameText.Format("env.weather.freeze_detail", frozen, info.BottleneckX, info.BottleneckY, PipeNetworkService.FluidName(info.Fluid)),
                        new Vector3(info.BottleneckX, 0f, info.BottleneckY));
                    GuidanceHooks.Raise(GuidanceHooks.EnvFirstPipeFrozen);
                    break;
                }
            }
            Revision++;
        }

        /// <summary>确定性随机（与世界种子、第几段排定绑定；不用 System.Random，跨运行时一致）。</summary>
        private struct Rng
        {
            private ulong _s;
            public Rng(int seed, int salt) { _s = (ulong)(uint)seed * 0xD1B54A32D192ED03UL ^ ((ulong)(uint)salt * 0x9E3779B97F4A7C15UL + 0x5851F42D4C957F2DUL); }

            public double Next()
            {
                _s += 0x9E3779B97F4A7C15UL;
                ulong z = _s;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                return (z >> 11) * (1.0 / (1UL << 53));
            }
        }

        // ── 日历来源（DEBT-FG7ENV01-01：时间条摘要 + 日历时间轴）──

        private sealed class WeatherCalendarSource : ICalendarSource
        {
            public CalendarKind Kind => CalendarKind.Weather;

            public void Collect(CampaignState state, double fromSeconds, double toSeconds, List<CalendarEntry> into)
            {
                WeatherFrontState d = state?.Weather?.Fronts;
                if (d?.Fronts == null)
                {
                    return;
                }
                double hz = Math.Max(1, GameClock.StepHz);
                long now = GameClock.Ticks;
                double nowS = now / hz;
                double visibleUntil = nowS + HorizonSeconds(state, now);
                double warnUntil = nowS + WarningDays * GameClock.DaySeconds;
                foreach (WeatherFrontRecord f in d.Fronts)
                {
                    if (f == null)
                    {
                        continue;
                    }
                    double s = f.StartTick / hz;
                    double e = f.EndTick / hz;
                    bool active = f.Serial == d.ActiveSerial;
                    if (e <= fromSeconds || s >= toSeconds || (!active && s > visibleUntil))
                    {
                        continue;
                    }
                    into.Add(new CalendarEntry
                    {
                        Kind = CalendarKind.Weather,
                        StartSeconds = s,
                        EndSeconds = e,
                        Id = "weather:" + f.Serial.ToString(CultureInfo.InvariantCulture),
                        Title = NameOf(f.Kind), // 时间条与日历自己按种类加 ☁ 标记
                        Detail = GameText.Format("env.cal.weather", GameClock.FormatDayTime(s), GameClock.FormatDayTime(e), NameOf(f.Kind), EffectText(f.Kind)),
                        Forecast = !active && s > warnUntil,
                    });
                }
            }
        }
    }
}
