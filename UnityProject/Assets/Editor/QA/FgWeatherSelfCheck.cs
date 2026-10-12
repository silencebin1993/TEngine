using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG7-ENV-03 天气与顶棚的自动验收（FG07 FGR-ENV-020 / 021 / 030；FG03 FGR-LOG-028 / 045；验收 FGT-ENV-004）。
    /// 全部起真实系统：真实统一时钟与世界模拟（天气由 <c>WorldSimulation.StepOnce</c> 推进、内核派生量在步首对齐）、正式入口 <see cref="WeatherService.Install"/>、
    /// 真实传送带 / 管线 / 战斗内核、真实施工（机器取料建顶棚）、真实监听站破译天气预报、真文件存读档、真 UXML 世界时间条 / 暂停菜单 / 叠加层。
    /// D1 数据；S1 排定（按种子确定、持续 0.5～1.5 日、频率随幕提高、第一段不早于第 3 日）；F1 预报（无天气预报 0.5 日预警 / 监听站 T2 破译后 3 日）；
    /// R1 顶棚（研究门槛、拖拽全有或全无、未探索 / 超上限 / 已有跳过、机器施工、下面照常建造、拆除全额返还、取消、隐藏与半透明、叠加层、建造模式拖拽）；
    /// E1 FGT-ENV-004 酸雨（露天 1% / 游戏小时、顶棚下不掉、污染 1 级加倍、耐久归零摧毁留虚影、汇总通知）；E2 沙暴（露天传送带 −25%、顶棚下不减、太阳能 ×0.4、露天炮塔探测 −20%、画面雾）；
    /// E3 寒潮（静止 1 游戏小时结冰阻断流动、伴热管不结冰、结冰画冰霜与警告、散热 +30%、结束自动解冻）；N1 负向（与静默夜叠加、整段跳过、没接入不推进、进行中不能再排、地下管线没有伴热档）；
    /// T1 暂停与 0.5x～3x；T2 观察一致；S2 天气进行中真文件存读档；U1 世界时间条 / 日历 / 英文；U2 暂停菜单天气效果滑条；P1 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgWeatherSelfCheck）。
    /// </summary>
    public static class FgWeatherSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 天气与顶棚")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            Line("\n[天气与顶棚] 排定与预报、酸雨 / 沙暴 / 寒潮效果与顶棚覆盖、管线结冰与伴热管、顶棚施工 / 拆除 / 隐藏 / 叠加层、倍速 / 观察 / 存读档、界面（FG7-ENV-03）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            bool fog = RenderSettings.fog;
            float fogDensity = RenderSettings.fogDensity;
            Color fogColor = RenderSettings.fogColor;
            FogMode fogMode = RenderSettings.fogMode;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgweather-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                DayNightCycle.ReloadTuning();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "天气在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），平时每步 O(1)；结冰判定在管线内核（AOT）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckSchedule);
                Step(CheckForecast);
                Step(CheckRoof);
                Step(CheckRoofDemolishConfirm);
                Step(CheckAcid);
                Step(CheckSandstorm);
                Step(CheckColdSnap);
                Step(CheckNegative);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSaveLoad);
                Step(CheckHud);
                Step(CheckPauseMenu);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"天气自检抛异常：{e}");
            }
            finally
            {
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                WeatherService.ResetForTests();
                RoofService.ResetForTests();
                WeatherView.ResetForTests();
                RoofView.ResetForTests();
                SilentNightService.ResetForTests();
                StandingRuleService.ResetForTests();
                SignalUplinkService.ResetForTests();
                SignalLinkService.ResetForTests();
                CalendarService.ResetForTests();
                DayNightCycle.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                IntelService.ResetSessionState();
                ResearchService.ResetForTests();
                OverlayService.ResetForTests();
                ResearchGate.TreeAvailableOverrideForTests = null;
                GameClock.ResetSession();
                StrategyClock.Reset();
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                RenderSettings.fog = fog;
                RenderSettings.fogDensity = fogDensity;
                RenderSettings.fogColor = fogColor;
                RenderSettings.fogMode = fogMode;
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [天气与顶棚] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Hz => Math.Max(1, GameClock.StepHz);
        private static long HourTicks => GameClock.TicksFor(GameClock.HourSeconds);
        private static long MinuteTicks => GameClock.TicksFor(GameClock.HourSeconds / 60.0);

        /// <summary>新世界（家园、种子地形），天气走正式入口；顶棚研究完成（需要时）；走一步让天气排好。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true, bool roofResearch = true, int scrap = 600)
        {
            WeatherService.ResetForTests();
            RoofService.ResetForTests();
            SilentNightService.ResetForTests();
            StandingRuleService.ResetForTests();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            CalendarService.ResetForTests();
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            DayNightCycle.ResetForTests();
            IntelService.ResetSessionState();
            ResearchService.ResetForTests();
            OverlayService.ResetForTests();
            CampaignState s = F.NewWorld(seed, observe, scrap);
            SignalCoreService.EnsureInitialized(s);
            SignalCoverageService.Invalidate();
            if (roofResearch)
            {
                ResearchService.CompleteForTests(s, "environment.roof", "logistics.heat_trace");
            }
            WeatherService.Install();
            WorldSimulation.StepMany(1);
            return s;
        }

        private static WeatherFrontState D(CampaignState s) => WeatherService.StateOf(s);

        /// <summary>长段落的进度（写 Unity 日志，单段跑卡住时看得到停在哪一步）。</summary>
        private static void Progress(string what) => Debug.Log($"[FgWeatherSelfCheck] {DateTime.Now:HH:mm:ss} {what}");

        private static void SkipToTick(CampaignState s, long target)
        {
            if (target > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, target - GameClock.Ticks);
            }
        }

        private static bool StepUntil(Func<bool> done, int maxSteps)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(1);
            }
            return done();
        }

        private static NotificationEntry LastOf(string typeId) => NotificationCenter.History.LastOrDefault(e => e?.Type != null && e.Type.Id == typeId);

        private static int CountOf(string typeId) => NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Sum(e => e.Count);

        private static string Detail(NotificationEntry e) => e?.Latest?.Detail ?? string.Empty;

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>核心附近一块完整的、每格都空着的顶棚块（4×4 对齐），返回块坐标；找不到 = null（按种子地形找，不写死坐标，B25）。</summary>
        private static (int Bx, int By)? FreeBlock(CampaignState s, float fromCore = 8f, float toCore = 26f, HashSet<long> avoid = null)
        {
            int n = RoofService.BlockCells;
            GridCell core = HomeGridService.CorePivot(s);
            int r0 = (int)(fromCore / n), r1 = (int)(toCore / n) + 1;
            int cbx = RoofService.BlockOf(core.X), cby = RoofService.BlockOf(core.Y);
            for (int r = r0; r <= r1; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        int bx = cbx + dx, by = cby + dy;
                        if (avoid != null && avoid.Contains(((long)bx << 32) ^ (uint)by))
                        {
                            continue;
                        }
                        bool ok = true;
                        for (int y = 0; y < n && ok; y++)
                        {
                            for (int x = 0; x < n && ok; x++)
                            {
                                var c = new GridCell(bx * n + x, by * n + y);
                                ok = HomeGridService.ValidatePlacement(s, "power_pole", c, 0, asPlayerPlacement: false, checkCost: false).Ok
                                     && HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pipe).Ok
                                     && Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(core.X, core.Y)) >= fromCore - 2f;
                            }
                        }
                        if (ok)
                        {
                            avoid?.Add(((long)bx << 32) ^ (uint)by);
                            return (bx, by);
                        }
                    }
                }
            }
            return null;
        }

        private static GridCell Cell(int bx, int by, int dx, int dy) => new GridCell(bx * RoofService.BlockCells + dx, by * RoofService.BlockCells + dy);

        /// <summary>真实施工：放下顶棚虚影后让机器取料建成（最多 <paramref name="maxSeconds"/> 游戏秒）。</summary>
        private static bool BuildRoof(CampaignState s, int bx, int by, int maxSeconds = 900)
        {
            int placed = RoofService.PlaceBox(s, Cell(bx, by, 0, 0), Cell(bx, by, 3, 3));
            return placed == 1 && StepUntil(() => RoofService.HasBlock(s, bx, by), maxSeconds * Hz);
        }

        // ── D1 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            float Tune(string id) => GridContent.TryGetTuning(id, out float v) ? v : float.NaN;
            bool tune = Math.Abs(Tune("env.weather.duration_min_days") - 0.5f) < 1e-4f && Math.Abs(Tune("env.weather.duration_max_days") - 1.5f) < 1e-4f
                        && Math.Abs(Tune("env.weather.warning_days") - 0.5f) < 1e-4f && Math.Abs(Tune("env.weather.forecast_days") - 3f) < 1e-4f
                        && Math.Abs(Tune("env.weather.acid_pct_per_hour") - 1f) < 1e-4f && Math.Abs(Tune("env.weather.sand_detect_factor") - 0.8f) < 1e-4f
                        && Math.Abs(Tune("env.weather.cold_dissipation_mult") - 1.3f) < 1e-4f && Math.Abs(Tune("env.roof.block_cells") - 4f) < 1e-4f
                        && Math.Abs(Tune("logistics.weather.sandstorm_slow_pct") - 25f) < 1e-4f && Math.Abs(Tune("power.solar.sandstorm_factor") - 0.4f) < 1e-4f;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_env as e;print('\\n'.join(l for l in e.dump_lines() if l.startswith('WXK') or l.startswith('WXA')))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("WXK\t", StringComparison.Ordinal) || l.StartsWith("WXA\t", StringComparison.Ordinal)).ToList();
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            var rt = t.TbWeather.DataList.Select(r => string.Join("\t", "WXK", r.Id, r.NameKey, r.EffectKey, r.Glyph, r.Color, r.Hazard, r.SortOrder))
                .Concat(t.TbWeatherAct.DataList.Select(r => string.Join("\t", "WXA", r.Act, Py(r.BadChance), Py(r.ClearMinDays), Py(r.ClearMaxDays), r.WAcid, r.WSand, r.WCold))).ToList();
            bool table = code == 0 && src.Count == rt.Count && src.OrderBy(x => x).SequenceEqual(rt.OrderBy(x => x))
                         && t.TbWeather.DataList.Select(r => r.Id).OrderBy(x => x).SequenceEqual(new[] { "acid_rain", "clear", "cold_snap", "sandstorm" });
            bool rising = t.TbWeatherAct.DataList.OrderBy(r => r.Act).Select(r => r.BadChance).Zip(t.TbWeatherAct.DataList.OrderBy(r => r.Act).Skip(1).Select(r => r.BadChance), (a, b) => b >= a).All(x => x);
            bool notify = new[] { "weather", "weather_warning", "weather_end", "weather_damage", "pipe_frozen" }.All(id => t.TbNotifyType.GetOrDefault(id)?.AwaySection == "weather");
            bool hooks = new[] { GuidanceHooks.EnvFirstWeatherWarning, GuidanceHooks.EnvFirstWeather, GuidanceHooks.EnvFirstRoof, GuidanceHooks.EnvFirstPipeFrozen }.All(h => GuidanceHooks.Known.Contains(h));
            bool codex = new[] { "codex.env.weather", "codex.env.roof" }.All(id => t.TbCodexEntry.GetOrDefault(id) is var c && c != null && GameText.Has(c.TitleKey) && GameText.Has(c.BodyKey));
            bool tools = GridContent.TryGetTool("roof", out GameConfig.fg.BuildTool roof) && roof.Kind == "roof" && roof.Category == "environment" && roof.UnlockRule == "research:environment.roof"
                         && GridContent.TryGetTool("pipe_t3", out GameConfig.fg.BuildTool heat) && heat.Kind == "pipe" && heat.Tier == PipeConst.HeatTracedTier && heat.UnlockRule == "research:logistics.heat_trace"
                         && t.TbBuildUpgrade.DataList.Any(u => u.FromId == "pipe_t2" && u.ToId == "pipe_t3");
            bool nodes = new[] { "environment.roof", "logistics.heat_trace", "signal.listening_t2" }.All(id => ResearchCatalog.TryGet(id, out ResearchNodeDef nd) && nd.IsReady && nd.Unlocks.Length > 0)
                         && t.TbBuildingTier.DataList.Any(r => r.Id == "listening_post.t2" && r.UnlockRule == "research:signal.listening_t2" && Math.Abs(r.Value - 3f) < 1e-4f);
            bool intel = IntelCatalog.TryGet(IntelCatalog.KindWeather, out IntelKindDef wk) && wk.IsReady && wk.ConditionArg == 2;
            bool keys = InputActionCatalog.TryGet(GameActionId.ToggleRoofs, out InputActionDef ta) && ta.Status == InputActionStatus.Wired
                        && InputActionCatalog.TryGet(GameActionId.OverlayRoof, out InputActionDef oa) && oa.Status == InputActionStatus.Wired && OverlayService.KindCount == 9;
            string[] textKeys =
            {
                "env.weather.name.acid_rain", "env.weather.effect.acid_rain", "env.weather.effect.sandstorm", "env.weather.effect.cold_snap", "env.weather.warn_detail",
                "env.weather.start_detail", "env.weather.end_detail", "env.weather.damage_detail", "env.weather.freeze_detail", "env.cal.weather", "env.hud.weather_clear",
                "env.weather.horizon_short", "env.roof.name", "build.tool.roof.desc", "ui.build.hint_roof", "env.roof.plan_status", "env.roof.reason.fog", "env.roof.hover",
                "overlay.name.roof", "overlay.legend.roof", "pause.weather_fx", "logistics.pipe.t3", "logistics.pipe.state.frozen", "input.action.toggle_roofs.name",
            };
            var missing = textKeys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string z) || string.IsNullOrWhiteSpace(z) || !GameText.TryGet(k, GameLanguage.En, out string e) || string.IsNullOrWhiteSpace(e)).ToList();
            Expect(tune && table && rising && notify && hooks && codex && tools && nodes && intel && keys && missing.Count == 0,
                $"D1 数据：调参（持续 0.5～1.5 日、预警 0.5 日 / 预报 3 日、酸雨 1%/小时、沙暴探测 ×0.8 / 传送带 −25% / 太阳能 ×0.4、寒潮散热 ×1.3、顶棚 4×4）{tune}；" +
                $"fg.TbWeather / fg.TbWeatherAct 与源数据逐行一致（源 {src.Count} 行 / 表 {rt.Count} 行）{table}，恶劣天气概率随幕不降 {rising}；通知 5 类进离家报告“天气与环境”段 {notify}；" +
                $"钩子 {hooks}；图鉴 {codex}；工具顶棚 / 伴热管与升级路线 {tools}；研究节点开放（顶棚 / 伴热管 / 监听站 T2，T2 预报 3 日）{nodes}；天气预报情报开放 {intel}；Ctrl+H / 第 9 种叠加层 {keys}" +
                (missing.Count > 0 ? "；缺文本：" + string.Join("、", missing) : string.Empty));
        }

        private static string Py(float v)
        {
            double d = Math.Round(v, 6);
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains(".") || s.Contains("E") ? s : s + ".0";
        }

        // ── S1 排定（FGR-ENV-020）────────────────────────────────────────────────

        private static void CheckSchedule()
        {
            CampaignState a = NewWorld(7301);
            WeatherFrontState da = D(a);
            string fa = string.Join(";", da.Fronts.Select(f => $"{f.Serial}:{f.Kind}:{f.StartTick}:{f.EndTick}"));
            CampaignState b = NewWorld(7301);
            string fb = string.Join(";", D(b).Fronts.Select(f => $"{f.Serial}:{f.Kind}:{f.StartTick}:{f.EndTick}"));
            CampaignState c = NewWorld(7302);
            string fc = string.Join(";", D(c).Fronts.Select(f => $"{f.Serial}:{f.Kind}:{f.StartTick}:{f.EndTick}"));
            double day = GameClock.DaySeconds;
            long horizon = GameClock.Ticks + GameClock.TicksFor(WeatherService.ScheduleDays * day);
            bool ahead = D(b).PlannedUntilTick >= horizon;
            // 长视野：60 日的排定（同一个排定函数），统计持续时长、第一段开始、种类与幕的频率。
            int n1 = WeatherService.PlanAheadForTests(b, 60);
            var fronts = D(b).Fronts.ToList();
            bool durations = fronts.All(f => (f.EndTick - f.StartTick) >= GameClock.TicksFor(0.5 * day) - 1 && (f.EndTick - f.StartTick) <= GameClock.TicksFor(1.5 * day) + 1);
            bool order = fronts.Zip(fronts.Skip(1), (x, y) => y.StartTick >= x.EndTick).All(ok => ok);
            bool firstDay = fronts.Count > 0 && fronts[0].StartTick >= GameClock.TicksFor((WeatherService.FirstDay - 1) * day);
            var kinds = fronts.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
            bool allKinds = new[] { WeatherService.AcidRain, WeatherService.Sandstorm, WeatherService.ColdSnap }.All(k => kinds.ContainsKey(k));
            CampaignState act3 = NewWorld(7301);
            act3.Progress.Act = 3;
            D(act3).Fronts = Array.Empty<WeatherFrontRecord>();
            D(act3).PlannedUntilTick = -1;
            D(act3).NextSerial = 1;
            int n3 = WeatherService.PlanAheadForTests(act3, 60);
            Expect(fa == fb && fa != fc && fa.Length > 0 && ahead && durations && order && firstDay && allKinds && n3 > n1,
                $"S1 排定：同一种子两次排定逐段一致、换种子不同（按 (世界种子, 序号) 的确定性随机，与世界生成 / 掉落 / 突袭分开）；提前排到 {WeatherService.ScheduleDays} 日后 {ahead}；" +
                $"60 日内第一幕 {n1} 段（酸雨 {kinds.GetValueOrDefault(WeatherService.AcidRain)} / 沙暴 {kinds.GetValueOrDefault(WeatherService.Sandstorm)} / 寒潮 {kinds.GetValueOrDefault(WeatherService.ColdSnap)}）、第三幕 {n3} 段（频率随幕提高）；" +
                $"每段持续 0.5～1.5 日 {durations}、互不重叠 {order}、第一段不早于第 {WeatherService.FirstDay} 日 {firstDay}");
        }

        // ── F1 预报（FGR-ENV-021）────────────────────────────────────────────────

        private static void CheckForecast()
        {
            CampaignState s = NewWorld(7311);
            F.PowerUp(s);
            WeatherFrontState d = D(s);
            WeatherService.PlanAheadForTests(s, 12);
            WeatherFrontRecord first = d.Fronts.FirstOrDefault();
            if (first == null)
            {
                Fail("F1 准备：排定里没有恶劣天气");
                return;
            }
            long warnAt = first.StartTick - GameClock.TicksFor(0.5 * GameClock.DaySeconds);
            double hz = Hz;
            // 预警前：日历 3 日视野里看不到（没有天气预报，只有 0.5 日预警）。
            SkipToTick(s, warnAt - 3);
            WorldSimulation.StepMany(1);
            double now = GameClock.GameSeconds;
            bool hiddenBefore = !CalendarService.Collect(s, now, now + 3 * GameClock.DaySeconds).Any(e => e.Kind == CalendarKind.Weather) && !first.Warned;
            int warn0 = CountOf("weather_warning");
            WorldSimulation.StepMany(4);
            now = GameClock.GameSeconds;
            var entries = CalendarService.Collect(s, now, now + 3 * GameClock.DaySeconds).Where(e => e.Kind == CalendarKind.Weather).ToList();
            NotificationEntry w = LastOf("weather_warning");
            bool warned = first.Warned && CountOf("weather_warning") == warn0 + 1 && Detail(w).Contains(WeatherService.NameOf(first.Kind)) && Detail(w).Contains("后开始")
                          && entries.Count == 1 && !entries[0].Forecast && entries[0].Detail.Contains(WeatherService.NameOf(first.Kind));
            string shortNote = WeatherService.HorizonNote(s);
            // 监听站 T2：真实破译“天气预报”→ 3 日视野（预报标记）。
            GridCell? at = F.FindFree(s, IntelService.TypeId, 6f, 40f);
            BuildingRecord post = at.HasValue ? F.Built(s, IntelService.TypeId, "wpost", at.Value) : null;
            if (post != null)
            {
                post.Tier = 2;
                HomeValleyPowerGrid.Recompute(s);
                F.Resync(s);
            }
            bool decoded = post != null && StepUntil(() => WeatherService.HasForecast(s, GameClock.Ticks, out _), 400 * Hz);
            now = GameClock.GameSeconds;
            WeatherService.PlanAheadForTests(s, 4);
            var later = CalendarService.Collect(s, now, now + 3 * GameClock.DaySeconds).Where(e => e.Kind == CalendarKind.Weather).ToList();
            bool horizon = Math.Abs(WeatherService.HorizonSeconds(s, GameClock.Ticks) - 3 * GameClock.DaySeconds) < 1e-6;
            IntelRecord rec = IntelService.StateOf(s)?.Records?.LastOrDefault(r => r != null && r.Kind == IntelCatalog.KindWeather);
            string summary = rec != null ? IntelService.Summary(s, rec, GameClock.Ticks) : string.Empty;
            int expected = d.Fronts.Count(f => f.EndTick > GameClock.Ticks && f.StartTick < GameClock.Ticks + GameClock.TicksFor(3 * GameClock.DaySeconds));
            bool longView = decoded && horizon && later.Count == expected && later.Any(e => e.Forecast || e.StartSeconds <= now + 0.5 * GameClock.DaySeconds)
                            && summary.Length > 0 && WeatherService.HorizonNote(s).Contains("已破译");
            Expect(hiddenBefore && warned && shortNote.Contains("T2") && longView,
                $"F1 预报：没有天气预报时 3 日视野里看不到（开始前 0.5 日才有）{hiddenBefore}；开始前 0.5 日（{GameClock.FormatDayTime(warnAt / hz)}）发天气预警“{Detail(w)}”、日历出现这一段 {warned}；" +
                $"页脚说明“{shortNote}”；监听站 T2 真实破译出天气预报后视野 3 日（{later.Count}/{expected} 段，情报内容“{summary}”）{longView}");
        }

        // ── R1 顶棚（FGR-ENV-030）────────────────────────────────────────────────

        private static void CheckRoof()
        {
            CampaignState locked = NewWorld(7321, roofResearch: false);
            GridCell core = HomeGridService.CorePivot(locked);
            ResearchGate.TreeAvailableOverrideForTests = () => true; // 研发树开放（全量自检里旧段按开放前的规则跑，这里显式打开门槛）
            RoofPlan lp = RoofService.PlanBox(locked, new GridCell(core.X + 10, core.Y + 10), new GridCell(core.X + 10, core.Y + 10));
            bool lockOk = !lp.Ok && lp.Reason.Contains("研究") && RoofService.PlaceBox(locked, new GridCell(core.X + 10, core.Y + 10), new GridCell(core.X + 10, core.Y + 10)) == 0;
            ResearchService.CompleteForTests(locked, "environment.roof");
            lockOk &= RoofService.PlanBox(locked, new GridCell(core.X + 10, core.Y + 10), new GridCell(core.X + 10, core.Y + 10)).Reason?.Contains("研究") != true;
            ResearchGate.TreeAvailableOverrideForTests = null;

            CampaignState s = NewWorld(7322);
            core = HomeGridService.CorePivot(s);
            var avoid = new HashSet<long>();
            (int Bx, int By)? blk = FreeBlock(s, 8f, 26f, avoid);
            if (!blk.HasValue)
            {
                Fail("R1 准备：核心附近找不到一块完整的空顶棚块");
                return;
            }
            int bx = blk.Value.Bx, by = blk.Value.By;
            // 先在块里放一座建筑：顶棚铺在建筑上方（不占地、不挡放置）。
            BuildingRecord under = F.Built(s, "power_pole", "roof_under", Cell(bx, by, 1, 1));
            RoofPlan far = RoofService.PlanBox(s, new GridCell(core.X + 900, core.Y + 900), new GridCell(core.X + 903, core.Y + 903));
            RoofPlan big = RoofService.PlanBox(s, new GridCell(core.X, core.Y), new GridCell(core.X + 40, core.Y + 40));
            bool reasons = !far.Ok && far.Fogged > 0 && far.Reason.Contains("未探索") && !big.Ok && big.Reason.Contains("最多");
            float scrap0 = s.Scrap;
            RoofPlan pv = RoofService.PlanBox(s, Cell(bx, by, 0, 0), Cell(bx, by, 3, 3));
            bool built = BuildRoof(s, bx, by);
            bool covered = RoofService.IsCovered(s, Cell(bx, by, 0, 0)) && RoofService.IsCovered(s, Cell(bx, by, 3, 3)) && !RoofService.IsCovered(s, Cell(bx, by, 4, 0))
                           && RoofService.IsBuildingCovered(s, under) && BeltNetworkService.Kernel.IsCovered(Cell(bx, by, 2, 2).X, Cell(bx, by, 2, 2).Y);
            // 下面照常建造：顶棚下还能放建筑 / 铺管线（顶棚不进格网）。
            bool buildUnder = HomeGridService.ValidatePlacement(s, "power_pole", Cell(bx, by, 3, 3), 0, asPlayerPlacement: false, checkCost: false).Ok
                              && HomeGridService.ValidatePipeCell(s, Cell(bx, by, 2, 0), PipePieceKind.Pipe).Ok;
            RoofPlan again = RoofService.PlanBox(s, Cell(bx, by, 0, 0), Cell(bx, by, 3, 3));
            bool skip = !again.Ok && again.Existing == 1 && again.Reason.Contains("已经");
            Expect(lockOk && reasons && pv.Ok && pv.New == 1 && pv.TotalScrap == RoofService.ScrapPerBlock && built && covered && buildUnder && skip,
                $"R1 顶棚放置：研究前锁定并写原因 {lockOk}；未探索处 / 一次超过 {RoofService.MaxBlocksPerDrag} 块整次拒绝并写原因 {reasons}；块里已有建筑也能铺（顶棚在上方、不占地）；" +
                $"机器取料施工建成 {built}（{RoofService.ScrapPerBlock} 废料 / 块）；覆盖正好 4×4、整座在下面的建筑算“在顶棚下”、传送带内核登记覆盖 {covered}；顶棚下照常放建筑 / 铺管线 {buildUnder}；已有的块再拖跳过 {skip}");

            // 拆除：只碰到一部分的框不拆；完整包住的拆掉、造价全额返还。
            float scrapBeforeDemolish = s.Scrap;
            int partial = RoofService.DemolishBox(s, Cell(bx, by, 1, 1), Cell(bx, by, 6, 6));
            int whole = RoofService.DemolishBox(s, Cell(bx, by, -1, -1), Cell(bx, by, 4, 4));
            WorldSimulation.StepMany(2);
            bool refund = Math.Abs(s.Scrap - scrapBeforeDemolish - RoofService.ScrapPerBlock) < 0.01f
                          || (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g != null && g.ResourceType == CampaignEconomyLedger.ResourceScrap);
            bool removed = partial == 0 && whole == 1 && !RoofService.HasBlock(s, bx, by) && !BeltNetworkService.Kernel.IsCovered(Cell(bx, by, 2, 2).X, Cell(bx, by, 2, 2).Y) && refund;
            // 取消规划：施工队列里取消，已到材料全额退回、规划消失。
            (int Bx, int By)? blk2 = FreeBlock(s, 8f, 30f, avoid);
            bool cancel = false;
            if (blk2.HasValue)
            {
                RoofService.PlaceBox(s, Cell(blk2.Value.Bx, blk2.Value.By, 0, 0), Cell(blk2.Value.Bx, blk2.Value.By, 0, 0));
                WorkOrderRecord order = s.WorkOrders.LastOrDefault(o => o != null && HomeValleyConstruction.IsRoofPlan(o.TargetId));
                bool planned = RoofService.IsPlannedBlock(s, blk2.Value.Bx, blk2.Value.By) && order != null;
                cancel = planned && PlanHistory.CancelSite(s, order) && RoofService.PlannedCount(s) == 0 && (s.Weather.Roofs.Plans?.Length ?? 0) == 0;
            }
            Expect(removed && cancel,
                $"R1 拆除与取消：只碰到一部分的框不拆（{partial}）、完整包住的拆掉（{whole}）、传送带覆盖登记撤销、造价全额返还；施工队列取消顶棚规划，规划与施工单一起消失 {cancel}");

            // 隐藏与半透明（画面）。
            BuildRoof(s, bx, by);
            RoofView.ResetForTests();
            RoofView.Refresh(s, true, false);
            bool normal = RoofView.Visible && Math.Abs(RoofView.Alpha - RoofView.NormalAlpha) < 1e-4f && RoofView.Quads == 1;
            RoofView.Refresh(s, true, true);
            bool translucent = RoofView.Visible && Math.Abs(RoofView.Alpha - RoofView.BuildAlpha) < 1e-4f;
            bool hidden = RoofService.ToggleHidden(s);
            RoofView.Refresh(s, true, true);
            bool hiddenOk = hidden && !RoofView.Visible && RoofService.LastMessage.Contains("隐藏");
            RoofService.ToggleHidden(s);
            RoofView.Refresh(s, false, false);
            bool notObserved = !RoofView.Visible;
            // 叠加层：顶棚覆盖（第 9 种），每块一个方框。
            OverlayService.Set(OverlayKind.Roof);
            OverlayService.RedrawNow();
            bool overlay = OverlayService.Active == OverlayKind.Roof && OverlayService.DrawnMarkers == RoofService.BuiltCount(s) + RoofService.PlannedCount(s)
                           && OverlayService.Labels.Any(l => l.Text.Contains("顶棚")) && OverlayService.Name(OverlayKind.Roof) == "顶棚覆盖";
            OverlayService.Set(OverlayKind.None);
            string hover = RoofService.HoverText(s, Cell(bx, by, 2, 2));
            Expect(normal && translucent && hiddenOk && notObserved && overlay && hover != null && hover.Contains("Ctrl+H"),
                $"R1 画面：普通视角近乎不透明（α {RoofView.NormalAlpha}）、建造模式半透明（α {RoofView.BuildAlpha}）{normal}/{translucent}；Ctrl+H 隐藏后不画、字幕写明 {hiddenOk}；家园不被观察时不画 {notObserved}；" +
                $"叠加层“顶棚覆盖”每块一个方框 + 标签 {overlay}；悬停“{hover}”");

            // 建造模式真实输入路径：选顶棚 → 按下 → 拖到对角 → 松开 = 放下一片顶棚虚影。
            (int Bx, int By)? blk3 = FreeBlock(s, 8f, 34f, avoid);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            bool drag = false;
            if (blk3.HasValue && mode != null)
            {
                mode.Open();
                mode.Select(RoofService.ToolId);
                GridCell a = Cell(blk3.Value.Bx, blk3.Value.By, 1, 1);
                GridCell b2 = Cell(blk3.Value.Bx, blk3.Value.By, 2, 2);
                mode.SetHover(s, a);
                mode.RefreshPreview(s);
                bool preview = mode.RoofPreview != null && mode.RoofPreview.Ok && mode.RoofPreview.New == 1;
                mode.PointerDown(s, a);
                mode.SetHover(s, b2);
                mode.PointerUp(s, b2);
                drag = preview && RoofService.IsPlannedBlock(s, blk3.Value.Bx, blk3.Value.By) && mode.StatusText.Contains("顶棚");
                mode.Close();
            }
            Expect(drag, $"R1 建造模式：选中顶棚指着一格就预览那一块（新铺 1 块），按住拖拽松开放下顶棚虚影，状态行写明“{mode?.StatusText}”");
        }

        // ── R2 拆除框里的顶棚与建筑同一时机执行（FG7-ENV-03 审查修复 P1）──────────────────

        private static void CheckRoofDemolishConfirm()
        {
            CampaignState s = NewWorld(7323, scrap: 2000);
            var avoid = new HashSet<long>();
            (int Bx, int By)? blk = FreeBlock(s, 10f, 30f, avoid);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            if (!blk.HasValue || mode == null)
            {
                Fail("R2 准备：找不到空顶棚块或建造模式");
                return;
            }
            int bx = blk.Value.Bx, by = blk.Value.By;
            bool roof = BuildRoof(s, bx, by);
            // 框里放超过确认门槛（grid.batch_demolish_confirm）座建筑：松手后必须先弹确认框。
            int need = GridContent.TuningInt("grid.batch_demolish_confirm") + 1;
            GridCell lo = Cell(bx, by, -6, -6), hi = Cell(bx, by, 9, 9);
            var poles = new List<string>();
            for (int y = lo.Y + 1; y <= hi.Y && poles.Count < need; y++)
            {
                for (int x = lo.X + 1; x <= hi.X && poles.Count < need; x++)
                {
                    var c = new GridCell(x, y);
                    if (HomeGridService.ValidatePlacement(s, "power_pole", c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        poles.Add(F.Built(s, "power_pole", "r2pole" + poles.Count.ToString(CultureInfo.InvariantCulture), c).BuildingId);
                    }
                }
            }
            GridCell covered = Cell(bx, by, 2, 2);
            int scrap0 = (int)s.Scrap;
            bool preview = false, asks = false, kept = false, both = false;
            string confirmLine = null, status = null;
            try
            {
                mode.Open();
                mode.SetDemolishMode(true);
                mode.PointerDown(s, lo);
                mode.SetHover(s, hi);
                mode.RefreshVisuals(s);
                preview = mode.BoxRoofBlocks == 1 && mode.BoxPlan != null && mode.BoxPlan.BuildingCount >= need;
                mode.PointerUp(s, hi);
                ConfirmRequest req = UiConfirmDialog.Current;
                confirmLine = req?.Lines.FirstOrDefault(l => l.Contains("顶棚"));
                asks = mode.PendingBatchConfirm && confirmLine != null && confirmLine.Contains("不能撤销") && RoofService.HasBlock(s, bx, by);
                UiConfirmDialog.Cancel();
                kept = RoofService.HasBlock(s, bx, by) && BeltNetworkService.Kernel.IsCovered(covered.X, covered.Y) && (int)s.Scrap == scrap0
                       && poles.All(id => HomeGridService.FindBuilding(s, id) != null && !HomeGridService.IsMarkedForDemolish(s, id));
                // 再框一次、确认：顶棚与建筑同一时机拆，状态行写明顶棚拆除不进撤销。
                mode.PointerDown(s, lo);
                mode.SetHover(s, hi);
                mode.PointerUp(s, hi);
                UiConfirmDialog.Confirm();
                status = mode.StatusText;
                both = !RoofService.HasBlock(s, bx, by) && !BeltNetworkService.Kernel.IsCovered(covered.X, covered.Y)
                       && poles.All(id => HomeGridService.FindBuilding(s, id) == null || HomeGridService.IsMarkedForDemolish(s, id))
                       && status != null && status.Contains("顶棚") && status.Contains("撤销");
            }
            finally
            {
                UiConfirmDialog.DiscardAll();
                mode.SetDemolishMode(false);
                mode.Close();
            }
            Expect(roof && poles.Count >= need && preview && asks && kept && both,
                $"R2 拆除框完整包住顶棚 + {poles.Count} 座建筑（超过确认门槛）：拖框预览写会拆 1 块顶棚 {preview}；松手先弹确认框、顶棚还在，确认框写“{confirmLine}” {asks}；" +
                $"点“取消”顶棚、覆盖登记、建筑、废料全部原样 {kept}；再框一次点“确认”顶棚与建筑同时拆，状态行“{status?.Replace("\n", " / ")}” {both}");
        }

        // ── E1 酸雨（FGT-ENV-004）────────────────────────────────────────────────

        private static void CheckAcid()
        {
            CampaignState s = NewWorld(7331);
            var avoid = new HashSet<long>();
            (int Bx, int By)? rb = FreeBlock(s, 8f, 24f, avoid);
            (int Bx, int By)? eb = FreeBlock(s, 8f, 30f, avoid);
            (int Bx, int By)? pb = FreeBlock(s, 8f, 36f, avoid);
            if (!rb.HasValue || !eb.HasValue || !pb.HasValue)
            {
                Fail("E1 准备：找不到三块空地");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            BuildingRecord exposed = F.Built(s, "power_pole", "acid_exposed", Cell(eb.Value.Bx, eb.Value.By, 1, 1));
            BuildingRecord coveredB = F.Built(s, "power_pole", "acid_covered", Cell(rb.Value.Bx, rb.Value.By, 1, 1));
            BuildingRecord polluted = F.Built(s, "power_pole", "acid_polluted", Cell(pb.Value.Bx, pb.Value.By, 1, 1));
            BuildingRecord doomed = F.Built(s, "power_pole", "acid_doomed", Cell(eb.Value.Bx, eb.Value.By, 3, 3));
            map.SetPollution(Cell(eb.Value.Bx, eb.Value.By, 1, 1), 0);
            map.SetPollution(Cell(rb.Value.Bx, rb.Value.By, 1, 1), 0);
            map.SetPollution(Cell(eb.Value.Bx, eb.Value.By, 3, 3), 0);
            map.SetPollution(Cell(pb.Value.Bx, pb.Value.By, 1, 1), 1);
            GridCell beltE = Cell(eb.Value.Bx, eb.Value.By, 0, 0);
            GridCell beltC = Cell(rb.Value.Bx, rb.Value.By, 0, 0);
            map.SetPollution(beltE, 0);
            map.SetPollution(beltC, 0);
            bool belts = BeltNetworkService.TryPlace(s, beltE, BeltDir.East, 0).Ok && BeltNetworkService.TryPlace(s, beltC, BeltDir.East, 0).Ok;
            bool roof = BuildRoof(s, rb.Value.Bx, rb.Value.By);
            float max = BuildingOps.MaxDurability("power_pole");
            exposed.Health = coveredB.Health = polluted.Health = max;
            doomed.Health = 0.5f;
            int beltMax = BeltNetworkService.MaxHp(0);
            int destroyed0 = CountOf("building_destroyed");
            int dmgNotify0 = CountOf("weather_damage");
            bool scheduled = WeatherService.ScheduleForTests(s, WeatherService.AcidRain, 2.0, 30 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsActive(s, WeatherService.AcidRain), 10 * Hz);
            long start = GameClock.Ticks;
            // 第 1 个游戏小时：一步一步真跑（批量按游戏分钟结算）。
            StepUntil(() => GameClock.Ticks >= start + HourTicks + 2, (int)HourTicks + 20);
            float lostE1 = max - RepairDroneService.DurabilityOf(s, exposed);
            float lostC1 = max - RepairDroneService.DurabilityOf(s, coveredB);
            float lostP1 = max - RepairDroneService.DurabilityOf(s, polluted);
            bool doomedGone = doomed.ConstructionState != BuildingConstructionState.Operational && CountOf("building_destroyed") > destroyed0;
            // 再跳 23 个游戏小时（按游戏分钟的批量照常补结算，每步最多 4 分钟），一共 24 小时。
            SkipToTick(s, start + 24 * HourTicks);
            StepUntil(() => D(s).AcidMinutes >= 24 * 60, 24 * 60);
            float lostE24 = max - RepairDroneService.DurabilityOf(s, exposed);
            float lostC24 = max - RepairDroneService.DurabilityOf(s, coveredB);
            int beltE24 = BeltNetworkService.DamageOf(beltE);
            int beltC24 = BeltNetworkService.DamageOf(beltC);
            // 传送带按格错开相位（审查修复：BeltAcid.Phase）：24 小时累计 = Σ 每分钟预算（平均精确 = ⌊最大耐久 × 24%⌋，T1 的 60 点不随相位变）。
            int phaseE = BeltAcid.Phase(beltE.X, beltE.Y);
            int expectBelt = 0;
            for (long k = 1; k <= 24 * 60; k++)
            {
                expectBelt += BeltAcid.MinuteDamage(WeatherService.PerMinute(beltMax), k + phaseE);
            }
            bool rate = Math.Abs(lostE1 - max / 100f) < 1e-3f && lostC1 == 0f && Math.Abs(lostP1 - 2f * max / 100f) < 1e-3f
                        && Math.Abs(lostE24 - 24f * max / 100f) < 1e-3f && lostC24 == 0f && beltE24 == expectBelt && beltC24 == 0;
            bool notify = CountOf("weather_damage") > dmgNotify0 && Detail(LastOf("weather_damage")).Contains("酸雨") && D(s).DamagedBuildings >= 2;
            string hover = HomeValleyBuildMode.DescribeBuilding(s, exposed);
            string hoverC = HomeValleyBuildMode.DescribeBuilding(s, coveredB);
            Expect(scheduled && belts && roof && rate && doomedGone && notify && hover.Contains("露天") && hoverC.Contains("在顶棚下"),
                $"E1 FGT-ENV-004 酸雨：露天建筑每游戏小时掉最大耐久的 1%（1 小时 {lostE1:0.##} / 24 小时 {lostE24:0.##}，最大 {max:0}），顶棚下 0（{lostC1:0.##} / {lostC24:0.##}），" +
                $"污染 1 级加倍（{lostP1:0.##}）；露天传送带 24 小时掉 {beltE24} 点（= ⌊{beltMax} × 24%⌋ = {expectBelt}），顶棚下 {beltC24}；耐久归零的建筑被摧毁、留下虚影并通知 {doomedGone}；" +
                $"损伤汇总通知“{Detail(LastOf("weather_damage"))}”{notify}；悬停写露天 / 在顶棚下");

            // 结束：转晴通知写明期间损失；之后不再掉。
            WeatherFrontRecord f = WeatherService.ActiveFront(s);
            if (f != null)
            {
                SkipToTick(s, f.EndTick - 2);
                StepUntil(() => !WeatherService.IsActive(s, WeatherService.AcidRain), 2000);
            }
            float hpAfterEnd = RepairDroneService.DurabilityOf(s, exposed);
            WorldSimulation.StepMany((int)HourTicks + 10);
            bool ended = !WeatherService.IsActive(s, WeatherService.AcidRain) && Math.Abs(RepairDroneService.DurabilityOf(s, exposed) - hpAfterEnd) < 1e-4f
                         && Detail(LastOf("weather_end")).Contains("受损");
            Expect(ended, $"E1 酸雨结束：转晴通知“{Detail(LastOf("weather_end"))}”写明期间受损 / 摧毁数量；之后露天建筑不再掉耐久");
        }

        // ── E2 沙暴 ───────────────────────────────────────────────────────────────

        private static void CheckSandstorm()
        {
            CampaignState s = NewWorld(7341);
            var avoid = new HashSet<long>();
            (int Bx, int By)? rb = FreeBlock(s, 8f, 24f, avoid);
            (int Bx, int By)? eb = FreeBlock(s, 8f, 30f, avoid);
            if (!rb.HasValue || !eb.HasValue)
            {
                Fail("E2 准备：找不到两块空地");
                return;
            }
            BuildRoof(s, rb.Value.Bx, rb.Value.By);
            BuildingRecord tExposed = F.Built(s, TurretCatalog.LightTypeId, "sand_t_exposed", Cell(eb.Value.Bx, eb.Value.By, 1, 1));
            BuildingRecord tCovered = F.Built(s, TurretCatalog.LightTypeId, "sand_t_covered", Cell(rb.Value.Bx, rb.Value.By, 1, 1));
            TurretService.Sync(s);
            float rangeE0 = TurretService.RangeOfTurret(s, tExposed.BuildingId);
            float rangeC0 = TurretService.RangeOfTurret(s, tCovered.BuildingId);
            float solar0 = PowerEnvironment.SolarFactor(s);
            BeltKernel k = BeltNetworkService.Kernel;
            int ipmOpen0 = k.EffectiveItemsPerMinute(0, false);
            WeatherService.ScheduleForTests(s, WeatherService.Sandstorm, 2.0, 6 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsSandstormNow(s), 10 * Hz);
            WorldSimulation.StepMany(2);
            float solar = PowerEnvironment.SolarFactor(s);
            int slow = k.ExposedSlowPercent;
            int ipmOpen = k.EffectiveItemsPerMinute(0, false);
            int ipmCovered = k.EffectiveItemsPerMinute(0, true);
            float rangeE = TurretService.RangeOfTurret(s, tExposed.BuildingId);
            float rangeC = TurretService.RangeOfTurret(s, tCovered.BuildingId);
            bool turretCovered = RoofService.IsBuildingCovered(s, tCovered);
            WeatherView.ResetForTests();
            WeatherView.Apply(WeatherService.Sandstorm, 1f);
            float fogFull = WeatherView.FogDensity;
            WeatherView.Apply(WeatherService.Sandstorm, 0.5f);
            float fogHalf = WeatherView.FogDensity;
            WeatherView.Apply(WeatherService.AcidRain, 1f);
            float fogAcid = WeatherView.FogDensity;
            WeatherView.Apply(WeatherService.Clear, 1f);
            bool fogRestored = WeatherView.FogDensity == 0f && WeatherView.EmissionRate == 0f;
            float fade0 = WeatherView.FadeFactor(s, D(s).Fronts[0].StartTick);
            float fadeMid = WeatherView.FadeFactor(s, D(s).Fronts[0].StartTick + HourTicks);
            bool visual = fogFull > fogHalf && fogHalf > 0f && fogAcid < fogFull && fogRestored && fade0 == 0f && Math.Abs(fadeMid - 1f) < 1e-4f;
            Expect(Math.Abs(solar - 0.4f * solar0) < 1e-4f && slow == BeltNetworkService.SandstormSlowPercent && ipmOpen < ipmOpen0 && ipmCovered == ipmOpen0
                   && rangeE0 > 0f && Math.Abs(rangeE - rangeE0 * 0.8f) < 1e-3f && turretCovered && Math.Abs(rangeC - rangeC0) < 1e-4f && visual,
                $"E2 沙暴：太阳能系数 {solar0:0.##} → {solar:0.##}（×0.4）；露天传送带减速 {slow}%（T1 每分钟 {ipmOpen0} → {ipmOpen} 件），顶棚下仍 {ipmCovered} 件；" +
                $"露天炮塔射程 {rangeE0:0.#} → {rangeE:0.#}（−20%），顶棚下的炮塔 {rangeC0:0.#} → {rangeC:0.#} 不变；画面雾：满强度 {fogFull:0.####}、半强度 {fogHalf:0.####}、酸雨 {fogAcid:0.####}，转晴恢复、淡入从 0 到 1 {visual}");
            WeatherFrontRecord f = WeatherService.ActiveFront(s);
            SkipToTick(s, f.EndTick - 2);
            StepUntil(() => !WeatherService.IsSandstormNow(s), 200);
            WorldSimulation.StepMany(2);
            TurretService.Sync(s);
            Expect(k.ExposedSlowPercent == 0 && Math.Abs(PowerEnvironment.SolarFactor(s) - solar0) < 1e-4f && Math.Abs(TurretService.RangeOfTurret(s, tExposed.BuildingId) - rangeE0) < 1e-4f,
                "E2 沙暴结束：传送带减速归零、太阳能与炮塔射程恢复");
            WeatherView.ResetForTests();
        }

        // ── E3 寒潮（FGR-LOG-045）────────────────────────────────────────────────

        private static void CheckColdSnap()
        {
            CampaignState s = NewWorld(7351);
            var avoid = new HashSet<long>();
            (int Bx, int By)? ab = FreeBlock(s, 8f, 24f, avoid);
            (int Bx, int By)? hb = FreeBlock(s, 8f, 30f, avoid);
            if (!ab.HasValue || !hb.HasValue)
            {
                Fail("E3 准备：找不到两块空地");
                return;
            }
            // 网络 A：水源上的泵 + 三格 T1 管线（没有需求 → 泵不抽、网络静止）。
            GridCell water = Cell(ab.Value.Bx, ab.Value.By, 0, 0);
            F.SetTerrain(s, water, "water");
            bool placedA = PipeNetworkService.TryPlace(s, water, PipePieceKind.Pump, 0, 0).Ok;
            for (int i = 1; i <= 3; i++)
            {
                placedA &= PipeNetworkService.TryPlace(s, Cell(ab.Value.Bx, ab.Value.By, i, 0), PipePieceKind.Pipe, 0, 0).Ok;
            }
            // 网络 H：三格伴热管（同样静止）。
            bool placedH = true;
            for (int i = 0; i < 3; i++)
            {
                placedH &= PipeNetworkService.TryPlace(s, Cell(hb.Value.Bx, hb.Value.By, i, 0), PipePieceKind.Pipe, PipeConst.HeatTracedTier, 0).Ok;
            }
            bool underground = !PipeNetworkService.TryPlace(s, Cell(hb.Value.Bx, hb.Value.By, 0, 2), PipePieceKind.Underground, PipeConst.HeatTracedTier, 0).Ok;
            PipeKernel pk = PipeNetworkService.Kernel;
            int Net(GridCell c) => pk.TryGetCellInfo(c.X, c.Y, out PipeCellInfo ci) ? ci.Network : -1;
            PipeNetInfo Info(GridCell c) { pk.TryGetNetworkInfo(Net(c), out PipeNetInfo ni); return ni; }
            GridCell aPipe = Cell(ab.Value.Bx, ab.Value.By, 2, 0);
            GridCell hPipe = Cell(hb.Value.Bx, hb.Value.By, 1, 0);
            CombatSiteCheck(s, out float bonus0);
            WorldSimulation.StepMany(Hz);
            pk.TryGetNetworkInfo(Net(aPipe), out PipeNetInfo filled);
            string fillNote = $"寒潮前：输送 {filled.DeliveredLpm:0} / 缓冲 {filled.BufferedLpm:0} 升/分、静止 {PipeNetworkService.IdleGameHours(filled.Id):0.##} 游戏小时";
            int frozenNotify0 = CountOf("pipe_frozen");
            WeatherService.ScheduleForTests(s, WeatherService.ColdSnap, 2.0, 4 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsActive(s, WeatherService.ColdSnap), 10 * Hz);
            WorldSimulation.StepMany(3);
            CombatSiteCheck(s, out float bonus);
            bool coldOn = s.Pipes.ColdSnap && pk.FreezeAfterSteps > 0;
            // 静止满 1 游戏小时之后结冰。
            bool frozen = StepUntil(() => Info(aPipe).Frozen, (int)(2 * HourTicks));
            PipeNetInfo ia = Info(aPipe);
            PipeNetInfo ih = Info(hPipe);
            double idleAtFreeze = PipeNetworkService.IdleGameHours(ia.Id);
            WorldSimulation.StepMany(Hz + 2);
            pk.PrepareRender(6, out int count, out _);
            PipeInstance[] inst = pk.PrepareRender(6, out count, out _);
            bool flags = Enumerable.Range(0, count).Any(i => inst[i].Ax == aPipe.X && inst[i].Ay == aPipe.Y && ((int)inst[i].Bw & 16) != 0)
                         && !Enumerable.Range(0, count).Any(i => inst[i].Ax == hPipe.X && inst[i].Ay == hPipe.Y && ((int)inst[i].Bw & 16) != 0)
                         && !Enumerable.Range(0, count).Any(i => inst[i].Ax == water.X && inst[i].Ay == water.Y && ((int)inst[i].Bw & 16) != 0);
            PipeNetworkService.TryDescribeHover(s, aPipe, out _, out string hover);
            NotificationEntry fn = LastOf("pipe_frozen");
            // 结冰阻断流动：这时给网络 A 挂一个需要水的消费者——一滴也拿不到。
            int cons = pk.AddConsumer(aPipe.X, aPipe.Y, 1, 120, 1);
            WorldSimulation.StepMany(Hz * 5);
            pk.TryGetConsumer(cons, out PipeConsumerInfo ci1);
            bool blocked = ci1.TotalDeliveredMl == 0 && (Info(aPipe).Issues & PipeNetIssue.Frozen) != 0;
            Expect(placedA && placedH && underground && coldOn && frozen && ia.FreezableCells > 0 && !ih.Frozen && ih.FreezableCells == 0 && idleAtFreeze >= 1.0
                   && flags && hover.Contains("结冰") && CountOf("pipe_frozen") > frozenNotify0 && Detail(fn).Contains("结冰") && blocked && Math.Abs(bonus - 0.3f) < 1e-4f && bonus0 == 0f,
                $"E3 寒潮（{fillNote}）：静止满 {idleAtFreeze:0.##} 游戏小时的 T1 网络结冰（内核判定，{ia.FreezableCells} 格会结冰）{frozen}，伴热管网络不结冰 {!ih.Frozen}，地下管线没有伴热档 {underground}；" +
                $"结冰的管线画冰霜（泵 / 伴热管不画）{flags}；悬停“{hover.Split('\n').FirstOrDefault(l => l.Contains("结冰"))}”；警告“{Detail(fn)}”；结冰阻断流动（消费者拿到 {ci1.TotalDeliveredMl} 毫升）{blocked}；" +
                $"家园武器散热加成 {bonus0} → {bonus}（+30%）");
            // 寒潮结束自动解冻：流动恢复、散热恢复。
            WeatherFrontRecord f = WeatherService.ActiveFront(s);
            SkipToTick(s, f.EndTick - 2);
            StepUntil(() => !WeatherService.IsActive(s, WeatherService.ColdSnap), 200);
            WorldSimulation.StepMany(Hz * 5);
            pk.TryGetConsumer(cons, out PipeConsumerInfo ci2);
            CombatSiteCheck(s, out float bonusAfter);
            Expect(!Info(aPipe).Frozen && ci2.TotalDeliveredMl > 0 && !s.Pipes.ColdSnap && pk.FreezeAfterSteps == 0 && bonusAfter == 0f && Detail(LastOf("weather_end")).Contains("解冻"),
                $"E3 寒潮结束：自动解冻、消费者开始拿到水（{ci2.TotalDeliveredMl} 毫升）、散热恢复；转晴通知“{Detail(LastOf("weather_end"))}”");
        }

        private static void CombatSiteCheck(CampaignState s, out float bonus)
        {
            bonus = WorldSimulation.Home?.Combat?.Kernel?.DissipationBonus ?? -1f;
        }

        // ── N1 负向 ───────────────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            // 与静默夜叠加：两者各自生效（静默夜期间酸雨照常结算）。
            CampaignState s = NewWorld(7361);
            SignalUplinkService.RealTimeForTests = () => 0f;
            SilentNightService.Install();
            WorldSimulation.StepMany(1);
            GridCell? at = F.FindFree(s, "power_pole", 8f, 30f);
            BuildingRecord b = at.HasValue ? F.Built(s, "power_pole", "neg_pole", at.Value) : null;
            if (b != null)
            {
                HomeGridService.MapFor(s).SetPollution(at.Value, 0);
            }
            SilentNightService.ScheduleForTests(s, 0.5, 4 * GameClock.HourSeconds, false);
            WeatherService.ScheduleForTests(s, WeatherService.AcidRain, 1.0, 3 * GameClock.HourSeconds);
            StepUntil(() => SilentNightService.IsActive(s) && WeatherService.IsActive(s, WeatherService.AcidRain), 10 * Hz);
            float hp0 = b != null ? RepairDroneService.DurabilityOf(s, b) : 0f;
            WorldSimulation.StepMany((int)HourTicks + 10);
            bool both = SilentNightService.IsActive(s) && WeatherService.IsActive(s, WeatherService.AcidRain) && b != null && RepairDroneService.DurabilityOf(s, b) < hp0
                        && !WeatherService.ScheduleForTests(s, WeatherService.Sandstorm, 1.0, 60.0);
            SilentNightService.ResetForTests();

            // 时间被整段跳过（只有自检跳时 / 损坏的旧档）：不补发效果、告警并移走。
            CampaignState k = NewWorld(7362);
            WeatherService.PlanAheadForTests(k, 12);
            WeatherFrontRecord first = D(k).Fronts.First();
            int skipped0 = WeatherService.SkippedCount, starts0 = WeatherService.StartCount;
            SkipToTick(k, first.EndTick + 10);
            WorldSimulation.StepMany(2);
            bool skip = WeatherService.SkippedCount == skipped0 + 1 && WeatherService.StartCount == starts0 && !D(k).Fronts.Contains(first) && WeatherService.ActiveKind(k) == WeatherService.Clear;

            // 没接正式入口：世界模拟不推进天气（自检之间复位后也不会误触发）。
            CampaignState u = NewWorld(7363);
            WeatherService.PlanAheadForTests(u, 12);
            WeatherFrontRecord uf = D(u).Fronts.First();
            WeatherService.ResetForTests();
            SkipToTick(u, uf.StartTick + 5);
            WorldSimulation.StepMany(2);
            bool idle = WeatherService.ActiveKind(u) == WeatherService.Clear && !uf.Warned && PowerEnvironment.SandstormProvider == null;

            // 酸雨中耐久归零（负向矩阵 FG07 第 5 节）：摧毁、留下虚影；自动重建规则照常（走既有的建筑摧毁入口）。
            CampaignState z = NewWorld(7364);
            GridCell? zc = F.FindFree(z, "power_pole", 8f, 30f);
            BuildingRecord zb = zc.HasValue ? F.Built(z, "power_pole", "zero_pole", zc.Value) : null;
            if (zb != null)
            {
                HomeGridService.MapFor(z).SetPollution(zc.Value, 0);
                zb.Health = 0.4f;
            }
            WeatherService.ScheduleForTests(z, WeatherService.AcidRain, 1.0, 3 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsActive(z, WeatherService.AcidRain), 10 * Hz);
            bool zero = StepUntil(() => zb != null && zb.ConstructionState != BuildingConstructionState.Operational, (int)HourTicks + 20)
                        && D(z).Destroyed >= 1 && HomeGridService.FindBuilding(z, zb.BuildingId) != null;
            Expect(both && skip && idle && zero,
                $"N1 负向：天气与静默夜叠加，两者各自生效（静默夜里酸雨照常结算）、进行中不能再排第二段 {both}；时间整段跳过一段天气 → 不补发效果、告警并移走 {skip}；" +
                $"没接正式入口时世界模拟不推进天气 {idle}；酸雨中耐久归零 → 摧毁、原地留下建筑虚影（自动重建 / 维修走既有入口）{zero}");
        }

        // ── T1 / T2 暂停与倍速、观察一致 ─────────────────────────────────────────

        private const int SegMinutes = 70;

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld, out int frames)
        {
            CampaignState s = NewWorld(seed, observe);
            pausedHeld = true;
            frames = 0;
            GridCell? at = F.FindFree(s, "power_pole", 8f, 30f);
            if (at.HasValue)
            {
                F.Built(s, "power_pole", "tm_pole", at.Value);
                HomeGridService.MapFor(s).SetPollution(at.Value, 0);
            }
            WeatherService.ScheduleForTests(s, WeatherService.AcidRain, 2.0, 40 * GameClock.HourSeconds / 60.0);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                WorldSimulation.SyncAllForSave();
                string p0 = JsonUtility.ToJson(s);
                long t0 = GameClock.Ticks;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                WorldSimulation.SyncAllForSave();
                pausedHeld = JsonUtility.ToJson(s) == p0 && GameClock.Ticks == t0 && WeatherService.StartCount == 0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.TicksFor(SegMinutes * GameClock.HourSeconds / 60.0);
            while (GameClock.Ticks < target && frames < 60 * 900)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            WorldSimulation.SyncAllForSave();
            return Masked(JsonUtility.ToJson(s)) + "|weather=" + JsonUtility.ToJson(D(s)) + "|start=" + WeatherService.StartCount + "|end=" + WeatherService.EndCount;
        }

        private static string Masked(string json) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(json, "[0-9a-f]{32}", "#"), ":build:[0-9a-f]{8}", ":build:#"), "\"[A-Za-z]*Utc\":\"[^\"]*\"", "\"Utc\":\"#\""),
            "\"HasLocation\":(true|false),\"RegionId\":\"[^\"]*\"", "\"HasLocation\":$1,\"RegionId\":\"#\"");

        private static string FirstDiff(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 80);
            return $"第 {i} 字符：…{a.Substring(from, Math.Min(160, a.Length - from))}… ≠ …{b.Substring(from, Math.Min(160, b.Length - from))}…";
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true;
            string diff = string.Empty;
            var shown = new List<string>();
            long steps = GameClock.TicksFor(SegMinutes * GameClock.HourSeconds / 60.0);
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(7371, true, speed, true, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"{speed}x 与 0.5x 不同：" + FirstDiff(reference, snap);
                }
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed - steps) <= 3f;
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && reference.Contains("start=1") && reference.Contains("end=1"),
                $"T1 暂停与 0.5x～3x（暂停 {paused}、逐字段一致 {same}、帧数成反比 {proportional}；{reference.Substring(Math.Max(0, reference.Length - 60))}）：暂停中 120 帧整份战役状态不动、天气不开始；{string.Join(" / ", shown)} 跑同样的预警 → 开始 → 酸雨按游戏分钟结算 → 结束，整份战役状态 JSON 逐字段一致，所需真实帧与倍速成反比" +
                (diff.Length == 0 ? string.Empty : "\n" + diff));
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(7372, true, 1f, false, out _, out _);
            string unseen = RunScenario(7372, false, 1f, false, out _, out _);
            Expect(seen == unseen, "T2 家园在观察与不观察时跨过整段酸雨（预警、开始、按游戏分钟结算、结束），整份战役状态逐字段一致（D-06 / FGR-BASE-021）" +
                                   (seen == unseen ? string.Empty : "\n" + FirstDiff(seen, unseen)));
        }

        // ── S2 天气进行中真文件存读档 ─────────────────────────────────────────────

        private static string Snap(CampaignState s, string poleId, GridCell belt, GridCell pipe)
        {
            BuildingRecord b = HomeGridService.FindBuilding(s, poleId);
            PipeKernel pk = PipeNetworkService.Kernel;
            int net = pk != null && pk.TryGetCellInfo(pipe.X, pipe.Y, out PipeCellInfo ci) ? ci.Network : -1;
            bool frozen = net >= 0 && pk.TryGetNetworkInfo(net, out PipeNetInfo ni) && ni.Frozen;
            return $"t={GameClock.Ticks}|fronts={JsonUtility.ToJson(D(s))}|roofs={JsonUtility.ToJson(s.Weather.Roofs)}|hp={R(b != null ? RepairDroneService.DurabilityOf(s, b) : -1)}" +
                   $"|belt={BeltNetworkService.DamageOf(belt)}|slow={BeltNetworkService.Kernel?.ExposedSlowPercent}|cold={s.Pipes.ColdSnap}|freeze={pk?.FreezeAfterSteps}|frozen={frozen}";
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(7381);
            var avoid = new HashSet<long>();
            (int Bx, int By)? eb = FreeBlock(s, 8f, 24f, avoid);
            (int Bx, int By)? rb = FreeBlock(s, 8f, 30f, avoid);
            if (!eb.HasValue || !rb.HasValue)
            {
                Fail("S2 准备：找不到空地");
                return;
            }
            BuildingRecord pole = F.Built(s, "power_pole", "save_pole", Cell(eb.Value.Bx, eb.Value.By, 1, 1));
            HomeGridService.MapFor(s).SetPollution(Cell(eb.Value.Bx, eb.Value.By, 1, 1), 0);
            GridCell belt = Cell(eb.Value.Bx, eb.Value.By, 3, 0);
            HomeGridService.MapFor(s).SetPollution(belt, 0);
            BeltNetworkService.TryPlace(s, belt, BeltDir.East, 0);
            GridCell pipe = Cell(eb.Value.Bx, eb.Value.By, 0, 3);
            PipeNetworkService.TryPlace(s, pipe, PipePieceKind.Pipe, 0, 0);
            BuildRoof(s, rb.Value.Bx, rb.Value.By);
            // 寒潮进行中（管线将要结冰），读档后接着跑；再接一段酸雨。
            WeatherService.ScheduleForTests(s, WeatherService.ColdSnap, 1.0, 2 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsActive(s, WeatherService.ColdSnap), 10 * Hz);
            WorldSimulation.StepMany((int)(HourTicks / 2));
            string before = Snap(s, pole.BuildingId, belt, pipe);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long until = GameClock.Ticks + 2 * HourTicks + GameClock.TicksFor(30);
            void Continue(CampaignState st)
            {
                WorldSimulation.StepMany((int)(HourTicks));
                WeatherService.ScheduleForTests(st, WeatherService.AcidRain, 600.0, 2 * GameClock.HourSeconds);
                WorldSimulation.StepMany((int)(until - GameClock.Ticks));
            }
            Continue(s);
            string continuous = Snap(s, pole.BuildingId, belt, pipe);
            string loaded = null, after = null;
            for (int round = 0; round < 2; round++)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                if (!rr.Success)
                {
                    Fail("S2 读档失败：" + rr.Message);
                    return;
                }
                CampaignState l = rr.State;
                CampaignSession.Set(Slot, l);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                string ld = Snap(l, pole.BuildingId, belt, pipe);
                Continue(l);
                string af = Snap(l, pole.BuildingId, belt, pipe);
                if (round == 0)
                {
                    loaded = ld;
                    after = af;
                }
                else if (ld != loaded || af != after)
                {
                    after = "第二次读档不同：" + af;
                }
            }
            // 读档那一刻内核派生量还没对齐（下一步开头才对齐）——比较读档后的存档字段，派生量在接着跑之后一致。
            string strip(string x) => System.Text.RegularExpressions.Regex.Replace(x ?? string.Empty, "\\|slow=[^|]*|\\|freeze=[^|]*|\\|frozen=[^|]*", string.Empty);
            Expect(save.Success && strip(loaded) == strip(before) && after == continuous && continuous.Contains("cold=False") && before.Contains("cold=True"),
                $"S2 天气进行中真文件存读档：读档后排定、进行中的寒潮、酸雨结算进度、顶棚、建筑 / 传送带耐久逐位一致；接着跑（结冰、解冻、再来一段酸雨）与不存档一直跑一致；同一存档读两次一致" +
                (strip(loaded) == strip(before) ? string.Empty : $"\n存档前 {before}\n读档后 {loaded}") + (after == continuous ? string.Empty : $"\n一直跑 {continuous}\n读档跑 {after}"));
        }

        // ── U1 世界时间条与日历 ───────────────────────────────────────────────────

        private static void CheckHud()
        {
            VisualElement root = F.MountUxml(UiKitFolder + "WorldBar.uxml", out GameObject go);
            try
            {
                var bar = go.AddComponent<WorldBarHudUIToolkit>();
                bar.BindView(root);
                CampaignState s = NewWorld(7391);
                WorldSimulation.StepMany(2);
                bar.Refresh();
                string clear = bar.CalendarExtraText;
                WeatherService.ScheduleForTests(s, WeatherService.Sandstorm, 0.2 * GameClock.DaySeconds, 0.6 * GameClock.DaySeconds);
                WorldSimulation.StepMany(2);
                bar.Refresh();
                string next = bar.CalendarExtraText;
                StepUntil(() => WeatherService.IsSandstormNow(s), (int)(0.25 * GameClock.DaySeconds * Hz));
                WorldSimulation.StepMany(2);
                bar.Refresh();
                string during = bar.CalendarExtraText;
                GameSettings.SetLanguage(GameLanguage.En);
                bar.Refresh();
                string en = bar.CalendarExtraText;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                double now = GameClock.GameSeconds;
                bool cal = CalendarService.HasSource(CalendarKind.Weather) && !CalendarService.PendingKindsSuffix().Contains("天气")
                           && CalendarService.Collect(s, now - 1, now + GameClock.DaySeconds).Any(e => e.Kind == CalendarKind.Weather && e.Title.Contains("沙暴") && e.Detail.Contains("太阳能"));
                Expect(clear.Contains("晴") && next.Contains("沙暴") && next.Contains("后") && during.Contains("沙暴") && !during.Contains("晴") && en.Contains("Sandstorm") && cal,
                    $"U1 世界时间条日历行：晴天“{clear}”；预警后“{next}”；沙暴中“{during}”；英文“{en}”；日历来源已注册（页脚不再写“后续加入”，时间轴有沙暴段与效果说明）{cal}");
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Object.DestroyImmediate(go);
            }
        }

        // ── U2 暂停菜单天气效果滑条 ───────────────────────────────────────────────

        private static void CheckPauseMenu()
        {
            VisualElement root = F.MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject go);
            try
            {
                GameSettings.ResetWeatherFx();
                var menu = go.AddComponent<PauseMenuUIToolkit>();
                menu.BindView(root);
                Slider slider = menu.WeatherFxSlider;
                bool range = slider != null && Mathf.Approximately(slider.lowValue, GameSettings.WeatherFxMin) && Mathf.Approximately(slider.highValue, GameSettings.WeatherFxMax)
                             && Mathf.Approximately(slider.value, GameSettings.WeatherFxDefault) && menu.WeatherFxLabelText.Contains("100%");
                slider.value = 0.4f;
                bool applied = Mathf.Approximately(GameSettings.WeatherFx, 0.4f) && menu.WeatherFxLabelText.Contains("40%");
                GameSettings.SetWeatherFx(0f);
                bool clamped = Mathf.Approximately(GameSettings.WeatherFx, GameSettings.WeatherFxMin);
                GameSettings.Load();
                bool persisted = Mathf.Approximately(GameSettings.WeatherFx, GameSettings.WeatherFxMin);
                GameSettings.ResetWeatherFx();
                menu.SyncWeatherFx();
                bool reset = Mathf.Approximately(GameSettings.WeatherFx, GameSettings.WeatherFxDefault) && Mathf.Approximately(slider.value, GameSettings.WeatherFxDefault)
                             && menu.WeatherFxResetButton != null && menu.WeatherFxResetButton.text.Contains("天气");
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "PauseMenu.uxml", "PauseMenuWindow");
                Expect(!probe.Contains("FAIL"), "U2 暂停菜单加了“天气视觉效果强度”滑条后布局探针仍通过：" + probe.Split('\n')[0]);
                Expect(range && applied && clamped && persisted && reset,
                    $"U2 暂停菜单“画面”天气视觉效果强度（FG07 第 4 节）：滑条范围 {GameSettings.WeatherFxMin:P0}～{GameSettings.WeatherFxMax:P0}、默认 100%，拖动立即生效、越界钳回、重开游戏保留、可恢复默认（{range}/{applied}/{clamped}/{persisted}/{reset}）");
            }
            finally
            {
                GameSettings.ResetWeatherFx();
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
        }

        // ── P1 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7395, scrap: 4000);
            // 规模（FG7-ENV-03 审查修复：按 FGR-SYS-041 后期规模）：核心南面 800 座露天建筑（整批登记），北面 101 行 × 150 格 ≈ 15,000 格传送带（三种等级轮流，
            // 每 10 行一条地下传送带 = 物流节点，每 20 行带上放满物品——摧毁时走物品落地 / 返还搬运单的批量路径；不接货源，避免几万件物品把搬运单引擎压成另一项测试）。
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            float poleMax = BuildingOps.MaxDurability("power_pole");
            var extra = new List<BuildingRecord>(800);
            for (int row = 0; row < 90 && extra.Count < 800; row++)
            {
                for (int col = 0; col < 40 && extra.Count < 800; col++)
                {
                    var c = new GridCell(core.X - 60 + col * 3, core.Y - 12 - row * 3);
                    if (map.OccupantAt(c) != null)
                    {
                        continue;
                    }
                    extra.Add(new BuildingRecord
                    {
                        BuildingId = HomeValleyLayout.RegionId + ":fgwx_perf" + extra.Count.ToString(CultureInfo.InvariantCulture),
                        BuildingTypeId = "power_pole",
                        RegionId = HomeValleyLayout.RegionId,
                        GridX = c.X,
                        GridY = c.Y,
                        Position = GridMath.FootprintCenter(c, 1, 1, 0),
                        Health = poleMax,
                        ConstructionState = BuildingConstructionState.Operational,
                        PowerPriority = 1,
                        PowerState = BuildingPowerState.NotApplicable,
                        Inventory = Array.Empty<CargoEntry>(),
                        QueueIds = Array.Empty<string>(),
                    });
                }
            }
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Concat(extra).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            int poles = extra.Count;
            Progress($"800 座建筑已登记（{poles}）");
            BeltKernel bk = BeltNetworkService.Kernel;
            int ox = core.X - 75, oy = core.Y + 48;
            for (int u = 0; u <= 100; u++)
            {
                int y = oy + u * 2;
                int tier = u % 3;
                bool under = u % 10 == 0;
                for (int x = 0; x < 150; x++)
                {
                    if (under && x == 62)
                    {
                        bk.AddUnderground(ox + 62, y, BeltDir.East, tier, 6);
                        x = 68;
                        continue;
                    }
                    bk.AddCell(ox + x, y, BeltDir.East, tier);
                }
                if (u % 20 == 0)
                {
                    for (int x = 0; x < 150; x++)
                    {
                        bk.InsertItemAt(ox + x, y, 3000, (ushort)(1 + (x & 7)));
                    }
                }
            }
            bk.EnsureTopology();
            BeltNetworkService.ApplyGridLayer(s, HomeGridService.MapFor(s));
            int belts = bk.CellCount;
            int beltItems = bk.ItemCount;
            Progress($"规模就绪：{poles} 座建筑、{belts} 格传送带、{beltItems} 件");
            const int n = 200000;
            var sw = Stopwatch.StartNew();
            long t = GameClock.Ticks;
            for (int i = 0; i < n; i++)
            {
                WeatherService.WorldStep(s, t);
            }
            double idleUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                WeatherService.PreStep(s);
            }
            double preUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            Progress($"每步检查 {idleUs:F3} μs、步首对齐 {preUs:F3} μs");

            // 批量摧毁是不是线性（审查修复 P1）：同一刻摧毁 500 格 / 2,000 格（带物品、含物流节点）各量一次，比每格耗时（平方级时 2,000 格的每格耗时是 500 格的 4 倍）。
            var all = new List<Unity.Mathematics.int3>();
            bk.CollectCells(all);
            var burstCells = new List<GridCell>();
            var burstAmounts = new List<int>();
            void Burst(int from, int count)
            {
                burstCells.Clear();
                burstAmounts.Clear();
                for (int i = from; i < from + count && i < all.Count; i++)
                {
                    burstCells.Add(new GridCell(all[i].x, all[i].y));
                    burstAmounts.Add(BeltNetworkService.MaxHp((all[i].z >> 8) & 0xFF));
                }
                BeltNetworkService.TryDamageBatch(s, burstCells, burstAmounts, null);
            }
            Burst(all.Count - 50, 50); // 预热（JIT），不计
            int dc0 = BeltNetworkService.DestroyedCount;
            sw.Restart();
            Burst(all.Count - 550, 500);
            double burst500 = sw.Elapsed.TotalMilliseconds;
            int d500 = BeltNetworkService.DestroyedCount - dc0;
            sw.Restart();
            Burst(all.Count - 2550, 2000);
            double burst2000 = sw.Elapsed.TotalMilliseconds;
            int d2000 = BeltNetworkService.DestroyedCount - dc0 - d500;
            double perCell500 = burst500 / Math.Max(1, d500);
            double perCell2000 = burst2000 / Math.Max(1, d2000);
            double linear = perCell2000 / Math.Max(1e-6, perCell500);
            WorldSimulation.StepMany(2);
            Progress($"批量摧毁 500 格 {burst500:F1} ms / 2000 格 {burst2000:F1} ms");

            // 正常酸雨（61 分钟）：内核逐格判定，热更层只处理交回的格。
            WeatherService.ResetBatchStats();
            WeatherService.ScheduleForTests(s, WeatherService.AcidRain, 1.0, 6 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsActive(s, WeatherService.AcidRain), 10 * Hz);
            StepUntil(() => D(s).AcidMinutes >= 61, (int)HourTicks + 200);
            double maxBatch = WeatherService.MaxBatchMs;
            int maxCand = WeatherService.MaxBeltCandidates;
            long cand = WeatherService.BeltCandidates;
            int sweeps = WeatherService.BeltSweepCount;
            int liveBelts = bk.CellCount;
            Progress($"正常酸雨 61 分钟：最慢 {maxBatch:F2} ms，扫 {sweeps} 次");

            // 大批传送带同时临近归零（审查修复 P1）：剩下的每格都扣到只剩 1 点，再下 70 分钟酸雨——按格错开的相位把归零分摊到不同分钟，每分钟一批批量摧毁。
            bk.CollectCells(all);
            var preset = new List<GridCell>(all.Count);
            var presetAmounts = new List<int>(all.Count);
            foreach (Unity.Mathematics.int3 c in all)
            {
                var cell = new GridCell(c.x, c.y);
                int left = BeltNetworkService.MaxHp((c.z >> 8) & 0xFF) - 1 - BeltNetworkService.DamageOf(cell);
                if (left > 0)
                {
                    preset.Add(cell);
                    presetAmounts.Add(left);
                }
            }
            sw.Restart();
            BeltNetworkService.TryDamageBatch(s, preset, presetAmounts, null);
            double presetMs = sw.Elapsed.TotalMilliseconds;
            int ghosts0 = (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).Count(p => p != null && p.Destroyed);
            int ground0 = s.GroundItems?.Length ?? 0;
            int haul0 = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o != null && o.Kind == WorkOrderKind.Haul && o.IssuerId == "return");
            int destroyed0 = BeltNetworkService.DestroyedCount;
            int failure0 = CountOf("failure");
            int merged0 = BeltNetworkService.BatchNotifyCount;
            int sweeps0 = WeatherService.BeltSweepCount;
            WeatherService.ResetBatchStats();
            long minute0 = D(s).AcidMinutes;
            StepUntil(() => D(s).AcidMinutes >= minute0 + 70, (int)(HourTicks * 70 / 60) + 300);
            double maxZeroBatch = WeatherService.MaxBatchMs;
            int maxDestroyed = WeatherService.MaxBeltsDestroyed;
            Progress($"大批归零 70 分钟：最慢 {maxZeroBatch:F2} ms，最多一分钟摧毁 {maxDestroyed} 格");
            int destroyed = BeltNetworkService.DestroyedCount - destroyed0;
            int ghosts = (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).Count(p => p != null && p.Destroyed) - ghosts0;
            int ground = (s.GroundItems?.Length ?? 0) - ground0;
            int haul = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o != null && o.Kind == WorkOrderKind.Haul && o.IssuerId == "return") - haul0;
            bool recordsLive = (s.Belts.Damage ?? Array.Empty<BeltDamageRecord>()).All(r => r != null && bk.HasCell(r.X, r.Y) && r.Lost == BeltNetworkService.DamageOf(new GridCell(r.X, r.Y)));
            int notices = CountOf("failure") - failure0;
            int batches = WeatherService.BeltSweepCount - sweeps0;
            bool merged = BeltNetworkService.BatchNotifyCount > merged0 && notices <= batches && BeltNetworkService.LastBatchNotifyText.Contains("同时被摧毁");
            bool dedupe = D(s).HitBelts.Length == D(s).DamagedBelts && D(s).HitBelts.Distinct().Count() == D(s).HitBelts.Length;
            Expect(poles >= 800 && belts >= 15000 && destroyed >= preset.Count / 2 && maxDestroyed > 0 && maxDestroyed * 10 <= destroyed && ghosts == destroyed && ground > 0 && haul > 0
                   && recordsLive && merged && dedupe && d500 >= 450 && d2000 >= 1800,
                $"P1 规模（FGR-SYS-041）：{poles} 座露天建筑 + {belts:N0} 格传送带 / {beltItems:N0} 件；先同一刻批量摧毁 500 / 2,000 格（实际 {d500} / {d2000}：地下传送带两端算一处）；" +
                $"剩下 {preset.Count:N0} 格扣到只剩 1 点后 70 分钟酸雨摧毁 {destroyed:N0} 格，最多的一分钟 {maxDestroyed} 格（按格错开相位，归零分摊到不同分钟）；" +
                $"每格一份虚影（+{ghosts}）、物品落地 +{ground} 份并开返还搬运单 +{haul} 张、受损表只剩活着的格且与运行时一致 {recordsLive}；" +
                $"{batches} 分钟只发了 {notices} 条摧毁通知（每分钟至多一条，多格合并：“{BeltNetworkService.LastBatchNotifyText}”）{merged}；受损去重表 {D(s).HitBelts.Length} = {D(s).DamagedBelts}");
            PerfLines.Add($"天气每步检查 {idleUs:F3} μs / 步，步首派生量对齐 {preUs:F3} μs / 步；酸雨按游戏分钟结算（{poles} 座建筑 + {liveBelts:N0} 格传送带）最慢一次 {maxBatch:F2} ms（61 分钟扫 {sweeps} 次，内核每分钟交回最多 {maxCand} 格、平均 {cand / Math.Max(1, sweeps)} 格）；" +
                          $"大批临近归零的 70 分钟里最慢一分钟 {maxZeroBatch:F2} ms（最多一分钟摧毁 {maxDestroyed} 格）；一次扣 {preset.Count:N0} 格耐久 {presetMs:F1} ms；" +
                          $"同一刻批量摧毁 500 格 {burst500:F1} ms / 2,000 格 {burst2000:F1} ms（每格 {perCell500 * 1000:F0} / {perCell2000 * 1000:F0} μs，比值 {linear:F2}）");
            PerfGate.Expect(poles >= 800 && belts >= 15000, $"P1 天气每步检查 {idleUs:F3} μs / 步（阈值 1 μs；O(1)）、步首派生量对齐 {preUs:F3} μs / 步（阈值 1 μs）、" +
                                  $"酸雨结算最慢一分钟 {maxBatch:F2} ms（阈值 4 ms；{poles} 座建筑 + {liveBelts:N0} 格传送带）、大批归零时最慢一分钟 {maxZeroBatch:F2} ms（阈值 8 ms = 120 帧一帧）、" +
                                  $"批量摧毁每格耗时 2,000 格 / 500 格 = {linear:F2}（阈值 2：线性约 1，平方级约 4）",
                new[] { PerfGate.Le(idleUs, 1.0, "天气每步 μs"), PerfGate.Le(preUs, 1.0, "步首对齐 μs"), PerfGate.Le(maxBatch, 4.0, "酸雨结算 ms"), PerfGate.Le(maxZeroBatch, 8.0, "大批归零 ms"),
                        PerfGate.Le(linear, 2.0, "批量摧毁线性比") }, Expect, Line);
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                UiConfirmDialog.DiscardAll();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
