using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
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
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG7-ENV-02 静默夜的自动验收（FG07 FGR-ENV-010～015、FGU-29；验收 FGT-ENV-002 / 003）。
    /// 全部起真实系统：真实统一时钟与世界模拟（静默夜由 <c>WorldSimulation.StepOnce</c> 推进）、正式入口 <see cref="SilentNightService.Install"/>、真实接入 / 安全模式、
    /// 真实常驻规则（静默夜预案经正式时间窗触发）、真实施工工单、真实突袭导演、真文件存读档、真 UXML 世界时间条 / 暂停菜单。
    /// D1 数据；C1 周期与预警（标准 5 日 / 建造者 7 日 / 严酷 4 日、旧档补排、日历来源与 HUD 摘要、常驻规则时间窗）；
    /// R1 FGT-ENV-002 第 3.2 节表格逐条（接入 / 远程命令 / 接入口 / 已有工作单与常驻规则 / 新施工规划 / 改规则配方优先级 / 镜头面板统计 / 突袭）+ 远征安全模式 + 结束（风暴残骸、总结、暂缓施工自动开工）；
    /// A1“静默夜开始”默认自动暂停（战略视角 / 接入直控中各一次，走 WorldSimulation.Frame 与真实暂停键）；R2 复制设置粘贴、撤销 / 重做设置被整步拒绝，粘贴蓝图虚影带配方不弹拒绝；
    /// X1 FGT-ENV-003 远征准备跨静默夜提示（进行中改说到几点结束）；B1 风暴残骸按种子（多种子、确定性）；T1 暂停与 0.5x～3x；T2 观察一致；S1 静默夜中真文件存读档；
    /// N1 负向（跳时整段错过、没接入时不推进、难度中途改只影响下一次）；U1 横幅 / 日历行（中英）；U2 暂停菜单压暗滑条；L1 画面压暗；P1 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgSilentNightSelfCheck）。
    /// </summary>
    public static class FgSilentNightSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        private const float FrameDt = 1f / 30f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 静默夜")]
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
            Line("\n[静默夜] 周期与预警、第 3.2 节期间规则逐条、远征安全模式与出发提示、风暴残骸与结束总结、倍速 / 观察 / 存读档、横幅与压暗（FG7-ENV-02）");
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
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            Light originalSun = RenderSettings.sun;
            Color originalAmbient = RenderSettings.ambientLight;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsilent-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => FrameDt;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "静默夜在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），每步 O(1)；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckSchedule);
                Step(CheckRulesDuringNight);
                Step(CheckAutoPauseAtStart);
                Step(CheckEditPathsDuringNight);
                Step(CheckExpeditionHint);
                Step(CheckDebrisSeeds);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSaveLoad);
                Step(CheckNegative);
                Step(CheckHud);
                Step(CheckPauseMenu);
                Step(CheckLighting);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"静默夜自检抛异常：{e}");
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
                SilentNightService.ResetForTests();
                StandingRuleService.ResetForTests();
                SignalUplinkService.ResetForTests();
                SignalLinkService.ResetForTests();
                DayNightLighting.ResetForTests();
                RenderSettings.sun = originalSun;
                RenderSettings.ambientLight = originalAmbient;
                CalendarService.ResetForTests();
                DayNightCycle.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
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
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
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
            Line($"  · [静默夜] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Hz => Math.Max(1, GameClock.StepHz);

        /// <summary>新世界（家园、种子地形），昼夜与静默夜都走正式入口；走一步让静默夜排好第一次。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true, string difficulty = "Standard")
        {
            SilentNightService.ResetForTests();
            StandingRuleService.ResetForTests();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            SignalLinkService.ResetForTests();
            CalendarService.ResetForTests();
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            DayNightCycle.ResetForTests();
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 400);
            s.DifficultyId = difficulty;
            SignalCoreService.EnsureInitialized(s);
            SignalCoverageService.Invalidate();
            DayNightCycle.Install();
            SilentNightService.Install();
            WorldSimulation.StepMany(1);
            return s;
        }

        private static SilentNightState D(CampaignState s) => SilentNightService.StateOf(s);

        private static void SkipTo(CampaignState s, int day, double hour)
        {
            SkipToTick(s, GameClock.TicksFor(GameClock.SecondsAt(day, hour)));
        }

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

        private static void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                InputRouter.DebugClearConsumedKeys();
                _fakeNow += FrameDt;
                WorldSimulation.Frame(FrameDt);
            }
        }

        private static double HourOf(long tick) => GameClock.HourOfDay(tick / (double)Hz);

        private static int HomeMachine(string chassis = null)
        {
            MachineRecord m = MachineRegistry.AllRecords.Where(r => r != null && r.IsAlive && r.RegionId == HomeValleyLayout.RegionId && !r.IsInFactory
                                                              && (chassis == null || r.ChassisId == chassis)).OrderBy(r => r.LogicId).FirstOrDefault();
            return m?.LogicId ?? 0;
        }

        private static string PlaceGhost(CampaignState s, string typeId = "power_pole")
        {
            GridCell? at = FgProductionSelfCheck.FindFree(s, typeId, 6f, 22f);
            if (!at.HasValue)
            {
                return null;
            }
            GridOpResult r = HomeGridService.TryPlace(s, typeId, at.Value, 0);
            return r.Outcome == GridOpResult.Kind.Placed ? r.BuildingId : null;
        }

        private static WorkOrderRecord BuildOrderOf(CampaignState s, string siteId) =>
            s.WorkOrders?.LastOrDefault(o => o != null && o.Kind == WorkOrderKind.Build && o.TargetId == siteId);

        private static NotificationEntry LastOf(string typeId) =>
            NotificationCenter.History.LastOrDefault(e => e?.Type != null && e.Type.Id == typeId);

        private static int CountOf(string typeId) =>
            NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Sum(e => e.Count);

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        // ── D1 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            float Tune(string id) => GridContent.TryGetTuning(id, out float v) ? v : float.NaN;
            bool tune = Math.Abs(Tune("env.silent.duration_hours") - 4f) < 1e-4f && Math.Abs(Tune("env.silent.warning_hours") - 1.2f) < 1e-4f
                        && Tune("env.silent.start_offset_hours") >= 0f && Tune("env.silent.start_offset_hours") + 4f <= (float)DayNightCycle.NightHours + 1e-4f
                        && Tune("env.silent.debris_min") <= Tune("env.silent.debris_max") && !float.IsNaN(Tune("env.silent.expedition_estimate_hours"));
            bool ui = UiTuningValues.TryGet("env.silent_dim_min", out float lo) && UiTuningValues.TryGet("env.silent_dim_max", out float hi)
                      && UiTuningValues.TryGet("env.silent_dim_default", out float de) && lo > 0f && lo <= de && de <= hi && hi < 1f;
            // 风暴残骸表：源数据（fgdata_env.STORM_DEBRIS）逐行等于运行时表。
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_env as e;print('\\n'.join(l for l in e.dump_lines() if l.startswith('SD')))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("SD\t", StringComparison.Ordinal)).ToList();
            var rt = ConfigSystem.Instance.Tables.TbStormDebris.DataList.Select(r => string.Join("\t", "SD", r.Id, r.ItemId, r.AmountMin, r.AmountMax, r.Weight, r.Rare, r.SortOrder)).ToList();
            bool table = code == 0 && src.Count == 3 && src.OrderBy(x => x).SequenceEqual(rt.OrderBy(x => x))
                         && ConfigSystem.Instance.Tables.TbStormDebris.DataList.All(r => ItemCatalog.TryGet(r.ItemId, out ItemDef def) && HomeValleyCargo.CanStore(ItemCatalog.ResourceTypeOf(r.ItemId)));
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            bool notify = new[] { "silent_night", "silent_night_warning", "silent_night_end" }.All(id => t.TbNotifyType.GetOrDefault(id) != null && t.TbNotifyType.GetOrDefault(id).AwaySection == "weather")
                          && t.TbNotifyType.GetOrDefault("silent_night").AutoPauseDefault == 1;
            bool hooks = new[] { GuidanceHooks.EnvFirstSilentWarning, GuidanceHooks.EnvFirstSilentNight, GuidanceHooks.EnvFirstStormDebris }.All(h => GuidanceHooks.Known.Contains(h));
            var codex = t.TbCodexEntry.GetOrDefault("codex.env.silent_night");
            bool codexOk = codex != null && codex.Hooks.Contains(GuidanceHooks.EnvFirstSilentNight) && GameText.Has(codex.TitleKey) && GameText.Has(codex.BodyKey);
            bool texts = new[] { "env.silent.banner", "env.cal.silent", "env.silent.warn_detail", "env.silent.reject.rules", "signal.command.silent_night", "env.silent.end.debris", "pause.silent_dim" }
                .All(GameText.Has) && !GameText.Get("difficulty.line.silent_night").Contains("上线后") && !GameText.Get("raid.trigger.silent_night.desc").Contains("上线后");
            Expect(tune && ui && table && notify && hooks && codexOk && texts,
                $"D1 数据：调参（持续 4 / 预警 1.2 游戏小时、开始偏移 + 持续落在夜里）{tune}，压暗设置范围 {ui}，fg.TbStormDebris 与源数据逐行一致且物品都能进仓库 {table}（源 {src.Count} 行 / 表 {rt.Count} 行），" +
                $"通知类型三类（进离家报告“天气与环境”段、静默夜开始默认自动暂停）{notify}，引导钩子 {hooks}，图鉴 {codexOk}，文本（难度 / 突袭说明去掉“上线后生效”）{texts}");
        }

        // ── C1 周期与预警（FGR-ENV-010）──────────────────────────────────────────

        private static void CheckSchedule()
        {
            var lines = new List<string>();
            bool ok = true;
            foreach ((string diff, int days) in new[] { ("Standard", 5), ("Builder", 7), ("Harsh", 4) })
            {
                CampaignState s = NewWorld(7201, true, diff);
                SilentNightState d = D(s);
                SilentNightService.PlanTicks(days, out long w, out long st, out long en);
                bool planned = d.NextDay == days && d.WarnTick == w && d.StartTick == st && d.EndTick == en && !d.Active && !d.Warned;
                bool hours = Math.Abs(HourOf(st) - 22.0) < 1e-3 && Math.Abs(HourOf(en) - 2.0) < 1e-3 && Math.Abs(HourOf(w) - 20.8) < 1e-3
                             && DayNightCycle.IsNight(st / (double)Hz) && DayNightCycle.IsNight((en - 1) / (double)Hz)
                             && en - st == GameClock.TicksFor(4 * GameClock.HourSeconds) && st - w == GameClock.TicksFor(1.2 * GameClock.HourSeconds);
                ok &= planned && hours;
                lines.Add($"{diff} 第 {d.NextDay} 日 {GameClock.FormatHhMm(st / (double)Hz)}～{GameClock.FormatHhMm(en / (double)Hz)}（预警 {GameClock.FormatHhMm(w / (double)Hz)}；1x 下持续 {(en - st) / (double)Hz:0} 秒、预警提前 {(st - w) / (double)Hz:0} 秒）");
            }
            Expect(ok, "C1 周期：按难度每 N 个游戏日一次（标准 5 / 建造者 7 / 严酷 4，读 fg.TbRaidDifficulty.silentNightDays），发生在夜里，持续 4 游戏小时，开始前 1.2 游戏小时预警：" + string.Join("；", lines));

            // 日历来源与常驻规则时间窗（DEBT-FG7ENV01-01 / DEBT-FG4ECO06-02 的正式接入）。
            CampaignState c = NewWorld(7202);
            SilentNightState cd = D(c);
            double now = GameClock.GameSeconds;
            bool next = CalendarService.TryNext(c, CalendarKind.SilentNight, now, 6 * GameClock.DaySeconds, out CalendarEntry e);
            bool calOk = next && Math.Abs(e.StartSeconds - cd.StartTick / (double)Hz) < 1e-6 && e.Title == GameText.Get("env.silent.title") && e.Detail.Contains("22:00")
                         && CalendarService.HasSource(CalendarKind.SilentNight) && CalendarService.PendingKindsSuffix() == "\n" + GameText.Get("env.cal.pending_weather");
            int marks = CalendarService.Collect(c, now, now + 14 * GameClock.DaySeconds).Count(x => x.Kind == CalendarKind.SilentNight);
            bool window = StandingRuleService.TrySilentWindow(c, out long ws, out long we) && ws == cd.StartTick && we == cd.EndTick;
            Expect(calOk && marks == 2 && window,
                $"C1 日历与时间窗：日历来源已注册（下一次 {e.Detail}），14 天视野里有第 5、10 日两次（{marks}），页脚只剩“天气预报后续加入”；常驻规则“静默夜预案”的正式时间窗 = 存档里的开始 / 结束步 {window}");

            // 旧档补排：没有静默夜计划的存档在第 7 日上午第一次推进 → 排到第 10 日；预警已过的第 5 日夜里 → 也排到第 10 日（不追溯）。
            CampaignState o = NewWorld(7203);
            D(o).NextDay = 0;
            SkipTo(o, 7, 9.0);
            WorldSimulation.StepMany(1);
            int fromDay7 = D(o).NextDay;
            D(o).NextDay = 0;
            CampaignState o2 = NewWorld(7204);
            D(o2).NextDay = 0;
            SkipTo(o2, 5, 21.0);
            WorldSimulation.StepMany(1);
            Expect(fromDay7 == 10 && D(o2).NextDay == 10 && !D(o2).Active,
                $"C1 旧档补排：没有计划的存档在第 7 日上午 → 第 {fromDay7} 日；第 5 日 21:00（预警已过）→ 第 {D(o2).NextDay} 日，不追溯、不立刻开始");
        }

        // ── R1 FGT-ENV-002：第 3.2 节表格逐条 ─────────────────────────────────────

        private static void CheckRulesDuringNight()
        {
            CampaignState s = NewWorld(7211);
            SilentNightState d = D(s);
            int erc1 = HomeMachine(HomeValleyLayout.Erc001ChassisId);
            int erc2 = HomeMachine(HomeValleyLayout.Erc002ChassisId);
            MachineOpResult expR = MachineRegistry.SpawnMachine(chassisId: HomeValleyLayout.Erc001ChassisId, blueprintId: HomeValleyLayout.BlueprintHaulerId,
                regionId: FracturedCityLayout.RegionId, position: new Vector2(0f, 0f), health: 100f, maxHealth: 100f, blueprintVersion: 1, loadoutSignature: null);
            int exp = expR.Success ? expR.LogicId : 0;
            // 常驻规则：静默夜预案（提前 30 秒），夜前可改（对照组）。
            bool created = StandingRuleService.TryCreateFromPreset(s, "preset.silent_default", out StandingRuleRecord rule, out string createMsg);
            bool addM = created && erc2 > 0 && StandingRuleService.TryAddMachine(s, rule.Serial, erc2, out _); // 预案要有动作（派机器回驻防点），否则规则报“没有动作”不能启用
            if (created && !rule.Enabled)
            {
                StandingRuleService.TrySetEnabled(s, rule.Serial, true, out _);
            }
            bool editBefore = addM && created && rule.Enabled && StandingRuleService.TrySetThreshold(s, rule.Serial, 29, out _) && StandingRuleService.TrySetThreshold(s, rule.Serial, 30, out _);
            bool recipeBefore = !ProductionService.TrySetRecipe(s, "nope", "nope", out string recipeBeforeMsg) && recipeBeforeMsg == GameText.Get("grid.reason.no_building");
            string ghostA = PlaceGhost(s);
            WorkOrderRecord orderA = BuildOrderOf(s, ghostA);
            Expect(erc1 > 0 && exp > 0 && created && editBefore && recipeBefore && orderA != null && SignalCoverageService.CanReceiveCommand(erc1),
                $"R1 准备：家园开局机器 #{erc1}、远征地点的机器 #{exp}、静默夜预案规则 R{rule?.Serial}（{createMsg}）、夜前放下的施工规划 {ghostA}；夜前改规则 / 下命令都可以（对照组）");

            // ── 预警 ──
            int warnN0 = CountOf("silent_night_warning");
            bool hookSeen0 = GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstSilentWarning);
            SkipToTick(s, d.WarnTick - 5);
            bool warned = StepUntil(() => d.Warned, 20);
            NotificationEntry warnN = LastOf("silent_night_warning");
            string warnText = warnN?.Latest?.DetailText ?? string.Empty;
            Expect(warned && GameClock.Ticks >= d.WarnTick && GameClock.Ticks <= d.WarnTick + 2 && CountOf("silent_night_warning") == warnN0 + 1 && !hookSeen0
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstSilentWarning) && warnText.Contains("22:00") && warnText.Contains("静默夜预案")
                   && SignalUplinkService.LastFeedbackText == GameText.Format("signal.link.warn.silent_night", "60"),
                $"R1 预警（FGR-ENV-010 / 015）：开始前 1.2 游戏小时那一步发“静默夜预警”通知（非阻塞说明会发生什么、要准备什么：“{warnText}”），HUD 提示“{SignalUplinkService.LastFeedbackText}”，第一次预警发引导钩子");

            // 信号接入 ERC-001，静默夜开始时应被弹回。
            CommitVia(erc1);
            bool inMachine = SignalPresence.CurrentMachineLogicId == erc1;
            int startCalls0 = SignalLinkService.SilentNightStartCalls;
            int triggers0 = RaidDirectorService.StateOf(s)?.TriggersReceived ?? 0;
            int startN0 = CountOf("silent_night");

            // ── 静默夜预案：开始前 30 秒执行 ──
            bool ruleOn = StepUntil(() => StandingRuleService.Find(s, rule.Serial)?.Active == true, (int)(d.StartTick - GameClock.Ticks) + 2);
            long ruleTick = GameClock.Ticks;
            Expect(ruleOn && ruleTick >= d.StartTick - 30L * Hz && ruleTick <= d.StartTick - 30L * Hz + 2 && !d.Active,
                $"R1 静默夜预案规则（卡片“必须同时交付”）：经正式时间窗在开始前 30 游戏秒触发（触发步 {ruleTick}，开始步 {d.StartTick}），此时静默夜还没开始");

            // ── 开始 ──
            bool started = StepUntil(() => d.Active, (int)(d.StartTick - GameClock.Ticks) + 2);
            long startedAt = GameClock.Ticks;
            Frames(2);
            Expect(started && startedAt >= d.StartTick && startedAt <= d.StartTick + 2 && SignalLinkService.IsSilentNight && d.Count == 1
                   && CountOf("silent_night") == startN0 + 1 && (LastOf("silent_night")?.Latest?.DetailText ?? string.Empty).Contains("02:00")
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstSilentNight),
                $"R1 开始：开始步进入静默夜（接入判定入口同时为真），发“静默夜开始”通知（{LastOf("silent_night")?.Latest?.DetailText}），第一次开始发引导钩子");

            // ① 接入：不可用；信号在机器里的，开始时强制弹回核心。
            UplinkRequestResult denied = SignalUplinkService.Request(erc2 > 0 ? erc2 : erc1, UplinkSource.MachineList, startCamera: false);
            Expect(inMachine && SignalPresence.AtCore && SignalLinkService.SilentNightStartCalls == startCalls0 + 1 && SignalLinkService.IsInSafeMode(s, erc1)
                   && SignalLinkService.SafeModeReason(s, erc1) == SignalLinkBreakReason.SilentNight && !denied.Accepted && denied.Failure == UplinkFailure.SilentNight,
                $"R1 ①接入：开始前信号在 #{erc1} 里 {inMachine} → 开始那一步弹回归还核心，机器进入安全模式（原因：静默夜），“静默夜开始”入口只调一次；静默夜里接入被拒（“{denied.Text}”）");

            // ② 远程命令（编队命令、驻防调动）：不可用。
            bool squad = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive).All(m => !SignalCoverageService.CanReceiveCommand(m.LogicId));
            bool garrison = !MachineRoster.TrySetGarrisonPoint(s, erc2 > 0 ? erc2 : erc1, null, out string gMsg) && gMsg.Contains("静默夜");
            bool role = !MachineRoster.TrySetRole(s, erc2 > 0 ? erc2 : erc1, MachineRole.Garrison, out string rMsg) && rMsg.Contains("驻防");
            Expect(squad && garrison && role,
                $"R1 ②远程命令：所有机器都收不到编队命令 / 右键派工（继续执行最后的命令）{squad}；驻防点“{gMsg}”；改岗位“{rMsg}”");

            // ③ 接入口：全部为空，机器和炮塔只跑本地常规固件（炮塔 / 靶场投影读同一个判定入口，见 FgTurretSelfCheck / FgTestRangeSelfCheck）。
            Expect(SignalUplinkService.CurrentMachine(s) == 0 && SignalPresence.AtCore && SignalLinkService.IsSilentNight,
                "R1 ③接入口：信号不在任何机器 / 炮塔 / 靶场投影里（接入口全空），都只跑本地常规固件");

            // ④ 已有工作单、常驻规则：继续执行。
            WorldSimulation.StepMany(Hz);
            StandingRuleRecord r1 = StandingRuleService.Find(s, rule.Serial);
            WorkOrderRecord a1 = BuildOrderOf(s, ghostA);
            bool aNotHeld = a1 != null && !SilentNightService.IsConstructionHeld(s, a1) && a1.FailureReason != SilentNightService.HoldReason;
            Expect(r1 != null && r1.Active && aNotHeld,
                $"R1 ④已有工作单与常驻规则继续执行：静默夜预案仍在执行 {r1?.Active}；夜前放下的施工单照常（状态 {a1?.State}，原因 {a1?.FailureReason ?? "无"}）");

            // ⑤ 新的施工规划：可以放虚影，但要等结束后才开工。
            string ghostB = PlaceGhost(s);
            WorldSimulation.StepMany(2);
            WorkOrderRecord b1 = BuildOrderOf(s, ghostB);
            var assign = HomeValleyWorkOrders.TryAssignConstruction(s, ghostB ?? string.Empty, erc1);
            string hold = b1 != null ? HomeValleyConstruction.DescribeStatus(s, b1) : string.Empty;
            Expect(ghostB != null && b1 != null && b1.State == WorkOrderState.Waiting && b1.FailureReason == SilentNightService.HoldReason && b1.AssignedMachineLogicId == 0
                   && SilentNightService.IsConstructionHeld(s, b1) && !assign.Success && hold == SilentNightService.HoldText(s) && hold.Contains("静默夜"),
                $"R1 ⑤新的施工规划：静默夜里放虚影成功（{ghostB}），施工单等待、不派机器（“{hold}”），选中机器去建也被拒");

            // ⑥ 修改规则、配方、优先级：不可用（界面上显示原因）。
            int denied0 = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied);
            int rej0 = SilentNightService.RejectCount;
            bool thr = !StandingRuleService.TrySetThreshold(s, rule.Serial, 45, out string thrMsg) && thrMsg.Contains("规则") && StandingRuleService.Find(s, rule.Serial).Threshold == 30;
            bool cre = !StandingRuleService.TryCreate(s, StandingRuleService.KindSilent, out StandingRuleRecord none, out _) && none == null;
            bool dis = !StandingRuleService.TrySetEnabled(s, rule.Serial, false, out _) && StandingRuleService.Find(s, rule.Serial).Enabled;
            bool rec = !ProductionService.TrySetRecipe(s, "nope", "nope", out string recMsg) && recMsg.Contains("配方");
            BuildingRecord anyPower = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.HasPriority(b.BuildingTypeId) && !HomeValleyController.IsPlannedGhost(b));
            bool pri = anyPower == null || (!BuildingOps.TrySetPriority(s, anyPower.BuildingId, anyPower.PowerPriority == 1 ? 2 : 1, out string priMsg) && priMsg.Contains("优先级"));
            bool cpri = !HomeValleyConstruction.SetPriority(s, b1?.WorkOrderId, 3) && !HomeValleyController.TrySetMachineWorkPriority(erc1, WorkOrderKind.Build, 1);
            int rejected = SilentNightService.RejectCount - rej0;
            int cues = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied) - denied0;
            Expect(thr && cre && dis && rec && pri && cpri && rejected >= 6 && cues >= rejected,
                $"R1 ⑥修改规则 / 配方 / 优先级被拒并写明原因：规则阈值“{thrMsg}”、新建规则、停用规则、配方“{recMsg}”、电力优先级、施工优先级、机器工作偏好（拒绝 {rejected} 次，拒绝音 + 字幕 {cues} 次）");

            // ⑦ 镜头、查看面板、统计：可用。
            WorldView.Observe(WorldSimulation.Home.SiteId);
            bool camera = WorldView.ObservedSiteId == WorldSimulation.Home.SiteId;
            bool cal = CalendarService.TryNext(s, CalendarKind.SilentNight, GameClock.GameSeconds, GameClock.DaySeconds, out CalendarEntry ce) && ce.StartSeconds <= GameClock.GameSeconds;
            bool stats = StandingRuleService.Find(s, rule.Serial) != null && HomeGridService.FindBuilding(s, ghostA) != null;
            Expect(camera && cal && stats && NotificationCenter.History.Count > 0,
                "R1 ⑦镜头、面板、统计照常可用：切换观察、日历（“进行中”）、通知历史、统计读取都不受影响");

            // ⑧ 突袭：必定发生。
            RaidDirectorState rd = RaidDirectorService.StateOf(s);
            bool raidQueued = rd != null && rd.TriggersReceived == triggers0 + 1;
            bool planned = StepUntil(() => RaidDirectorService.Plans(s).Any(p => p.Trigger == RaidCatalog.TriggerSilentNight), Hz * 20);
            Expect(raidQueued && planned,
                $"R1 ⑧突袭必定发生：开始那一步向突袭导演发出静默夜触发（收到 {rd?.TriggersReceived - triggers0} 次），导演排出一支“静默夜”突袭（DEBT-FG6DEF04-01 真实触发）");

            // 远征中遇到静默夜（FGT-ENV-003 前半）。
            Expect(SignalLinkService.IsInSafeMode(s, exp) && SignalLinkService.SafeModeReason(s, exp) == SignalLinkBreakReason.SilentNight
                   && d.ExpeditionSafeIds.Contains(exp) && (LastOf("silent_night")?.Latest?.DetailText ?? string.Empty).Contains("远征队"),
                $"R1 远征中遇到静默夜（FGR-ENV-013）：远征地点的机器 #{exp} 进入安全模式（原因：静默夜），继续执行最后的命令（收不到新命令）；开始通知写明“远征队进入安全模式”");

            // 画面压暗淡入。
            float dim0 = SilentNightService.DimFactor(s, d.StartTick / (double)Hz);
            float dimMid = SilentNightService.DimFactor(s, (d.StartTick + d.EndTick) / 2.0 / Hz);
            Expect(dim0 < 0.01f && Math.Abs(dimMid - 1f) < 1e-4f, $"R1 画面压暗：开始那一刻 {dim0:0.##}，淡入后 {dimMid:0.##}（纯表现，读统一时钟）");

            // ── 结束 ──
            int endN0 = CountOf("silent_night_end");
            int ground0 = s.GroundItems?.Length ?? 0;
            SkipToTick(s, d.EndTick - 3);
            bool ended = StepUntil(() => !d.Active, 10);
            WorldSimulation.StepMany(2);
            StandingRuleRecord r2 = StandingRuleService.Find(s, rule.Serial);
            WorkOrderRecord b2 = BuildOrderOf(s, ghostB);
            string summary = LastOf("silent_night_end")?.Latest?.DetailText ?? string.Empty;
            var debris = d.LastDebrisIds.Select(id => HomeValleyCargo.FindGroundItem(s, id)).ToList();
            int min = (int)Math.Round(GridContent.Tuning("env.silent.debris_min"));
            int max = (int)Math.Round(GridContent.Tuning("env.silent.debris_max"));
            GridCell core = HomeGridService.CorePivot(s);
            bool debrisOk = debris.Count >= Math.Min(1, min) && debris.Count <= max + 1 && debris.All(g => g != null && g.RegionId == HomeValleyLayout.RegionId
                                && WorldGenQuery.IsWalkableGenerated(s, Mathf.RoundToInt(g.Position.x), Mathf.RoundToInt(g.Position.y))
                                && Vector2.Distance(g.Position, new Vector2(core.X, core.Y)) <= GridContent.Tuning("env.silent.debris_ring_max") + 1.5f
                                && s.WorkOrders.Any(o => o != null && o.Kind == WorkOrderKind.Haul && o.TargetId == g.GroundItemId));
            Expect(ended && CountOf("silent_night_end") == endN0 + 1 && summary == d.LastSummary && summary.Contains("风暴残骸") && (summary.Contains("突袭") || summary.Contains("没有突袭"))
                   && summary.Contains("损失"),
                $"R1 结束总结通知（卡片“必须同时交付”）：“{summary}”");
            Expect(debrisOk && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstStormDebris) && (s.GroundItems?.Length ?? 0) == ground0 + debris.Count,
                $"R1 风暴残骸（FGR-ENV-014）：结束时按种子在家园地面放了 {debris.Count} 处（{string.Join("、", debris.Select(g => g == null ? "缺" : $"{g.ResourceType}×{g.Amount}@({g.Position.x:0},{g.Position.y:0})"))}），都在可走的空地上、离核心不超过 {GridContent.Tuning("env.silent.debris_ring_max")} 格，各开了一张搬运单让机器搬回");
            bool released = b2 != null && b2.FailureReason != SilentNightService.HoldReason && b2.State != WorkOrderState.Waiting;
            Expect(released && r2 != null && !r2.Active && d.NextDay == 10 && !SignalLinkService.IsSilentNight && SignalCoverageService.CanReceiveCommand(erc1)
                   && StandingRuleService.TrySetThreshold(s, rule.Serial, 31, out _),
                $"R1 结束后恢复：静默夜里放的施工自动开工（{b2?.State}）、静默夜预案恢复原状、下一次排到第 {d.NextDay} 日、接入与远程命令、改规则恢复");
            int guard = 0;
            while (SignalLinkService.IsInSafeMode(s, exp) && guard++ < Hz * 6)
            {
                WorldSimulation.StepMany(1);
            }
            SignalLinkBreakReason cause = MachineRegistry.TryGetRecord(exp, out MachineRecord expRec) ? SignalLinkService.CauseAt(s, expRec) : SignalLinkBreakReason.None;
            bool exitOk = cause == SignalLinkBreakReason.None ? !SignalLinkService.IsInSafeMode(s, exp) : SignalLinkService.IsInSafeMode(s, exp) && SignalLinkService.SafeModeReason(s, exp) == SignalLinkBreakReason.SilentNight;
            Expect(exitOk, $"R1 静默夜结束后远征队按断链条件处理：还有别的断链条件（{cause}，例如远征地点在覆盖外）时继续安全模式，都消失后 2 游戏秒退出（实测 {SignalLinkService.IsInSafeMode(s, exp)}）");
        }

        private static void CommitVia(int logicId)
        {
            if (SignalPresence.CurrentMachineLogicId == logicId)
            {
                return;
            }
            UplinkRequestResult r = SignalUplinkService.Request(logicId, UplinkSource.MachineList, startCamera: false);
            if (!r.Accepted)
            {
                Fail($"测试准备：接入 #{logicId} 被拒（{r.Failure}：{r.Text}）");
                return;
            }
            int n = 0;
            while (SignalUplinkService.IsPending && n++ < 120)
            {
                Frames(1);
            }
            if (SignalPresence.CurrentMachineLogicId != logicId)
            {
                Fail($"测试准备：接入 #{logicId} 没有完成（信号在 {SignalPresence.CurrentMachineLogicId}，最后反馈“{SignalUplinkService.LastFeedbackText}”）");
            }
        }

        // ── A1 “静默夜开始”默认自动暂停（FGR-UX-021 / B08；冒烟 FG1-SIG-04 / StepLinkSilentBroken 同一路径；第一轮复审 P0）──

        private sealed class KeyReader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static bool FramesUntil(Func<bool> done, int maxFrames)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (done())
                {
                    return true;
                }
                Frames(1);
            }
            return done();
        }

        /// <summary>
        /// 正式流程里 Play 的自动暂停处理器是 GameRoot.AutoPauseForNotification（= Application.isPlaying &amp;&amp; TryAutoPauseWorld()）；编辑模式下直接挂同一个判定（[M6 出口] H4 同一做法）。
        /// 全程走 <see cref="WorldSimulation.Frame"/>（镜头、输入、统一时钟与 Play 同一条路径：接入视角由 WorldView.FrameBegin 按镜头模式设直控锁），暂停键经 InputRouter 的按键读取。
        /// </summary>
        private static void CheckAutoPauseAtStart()
        {
            Func<bool> keep = NotificationCenter.AutoPauseHandler;
            var keys = new KeyReader();
            try
            {
                NotificationCenter.AutoPauseHandler = GameRoot.TryAutoPauseWorld;
                foreach (bool uplinked in new[] { false, true })
                {
                    CampaignState s = NewWorld(uplinked ? 7232 : 7231);
                    InputRouter.DebugSetReader(keys); // 载入世界会 InputRouter.Reset() 把读取后端复位成真键盘，载入之后再换成脚本按键
                    SilentNightState d = D(s);
                    GameClock.SetPaused(false);
                    int m = HomeMachine(HomeValleyLayout.Erc001ChassisId);
                    bool direct = true;
                    string prep = "战略视角、信号在核心";
                    if (uplinked)
                    {
                        UplinkRequestResult r = SignalUplinkService.Request(m, UplinkSource.MachineList, startCamera: true);
                        direct = r.Accepted && FramesUntil(() => SignalPresence.CurrentMachineLogicId == m && WorldView.Director.Mode == ViewMode.Direct && GameClock.DirectLocked, 300);
                        prep = $"接入直控 #{m} 中（镜头在接入视角、时钟锁 1x：{direct}，{r.Text}）";
                    }
                    int p0 = NotificationCenter.AutoPauseCount;
                    SkipToTick(s, d.StartTick - 4);
                    bool started = FramesUntil(() => d.Active, 60);
                    long at = GameClock.Ticks;
                    bool paused = started && GameRoot.IsWorldPaused && NotificationCenter.AutoPauseCount == p0 + 1 && at >= d.StartTick && at <= d.StartTick + 1
                                  && !GameClock.DirectLocked && SignalPresence.AtCore;
                    // 暂停中镜头照常退回战略视角（接入视角下受控机没了；暂停中过渡照常推进），时间不走；暂停键在战略视角生效。
                    bool strategy = FramesUntil(() => WorldView.Director.Mode == ViewMode.Strategy, 120);
                    bool stillPaused = GameRoot.IsWorldPaused && GameClock.Ticks == at;
                    keys.Down = GameSettings.KeyBindings.GetKey(GameActionId.TogglePause);
                    Frames(1);
                    keys.Down = KeyCode.None;
                    bool resumed = !GameRoot.IsWorldPaused;
                    long t = GameClock.Ticks;
                    Frames(3);
                    bool moving = GameClock.Ticks > t;
                    Expect(direct && paused && strategy && stillPaused && resumed && moving,
                        $"A1 {prep}遇到静默夜开始：“静默夜开始”默认自动暂停（FGR-UX-021），停在开始那一步（第 {at} 步，开始步 {d.StartTick}）" +
                        (uplinked ? "；信号弹回核心、直控锁当步释放（FG-GAP-114“接入中不自动暂停”只管仍在接入的情形）" : string.Empty) +
                        $"；暂停中镜头回到战略视角、时间不走；按暂停键继续后照常推进（{direct}/{paused}/{strategy}/{stillPaused}/{resumed}/{moving}）");
                }
            }
            finally
            {
                NotificationCenter.AutoPauseHandler = keep;
                InputRouter.DebugSetReader(null);
                GameClock.SetPaused(false);
                GameClock.SetDirectLocked(false);
            }
        }

        // ── R2 静默夜里的其余修改入口：复制设置粘贴、撤销 / 重做设置、粘贴蓝图虚影（第 3.2 节；第一轮复审 P1）──

        private const string Furnace = "refinery_furnace";

        private static string RecipeOf(CampaignState s, BuildingRecord b) =>
            b != null && ProductionService.TryGet(s, b.BuildingId, out ProductionService.Producer p) ? p.Recipe?.Id : null;

        private static void CheckEditPathsDuringNight()
        {
            CampaignState s = NewWorld(7214);
            SilentNightState d = D(s);
            GridCell? c1 = FgProductionSelfCheck.FindFree(s, Furnace, 6f, 10f);
            BuildingRecord f1 = c1.HasValue ? FgProductionSelfCheck.Built(s, Furnace, "silent_f1", c1.Value) : null;
            GridCell? c2 = FgProductionSelfCheck.FindFree(s, Furnace, 13f, 17f);
            BuildingRecord f2 = c2.HasValue ? FgProductionSelfCheck.Built(s, Furnace, "silent_f2", c2.Value) : null;
            if (f1 == null || f2 == null)
            {
                Fail("R2 测试准备：放不下两座建成的精炼炉");
                return;
            }
            var cell1 = new GridCell(f1.GridX, f1.GridY);
            var cell2 = new GridCell(f2.GridX, f2.GridY);
            // 夜前（对照组）：f1 = 合金矿 / 优先级 2，f2 = 废料合金；把 f1 的设置粘贴到 f2（进撤销栈）；再把 f1 改成废料合金 / 优先级 3 备用。
            bool r1 = ProductionService.TrySetRecipe(s, f1.BuildingId, "alloy_ore", out string m1);
            bool p1 = BuildingOps.TrySetPriority(s, f1.BuildingId, 2, out string m2);
            bool r2 = ProductionService.TrySetRecipe(s, f2.BuildingId, "alloy_scrap", out string m3);
            bool setup = r1 && p1 && r2;
            int prio2 = f2.PowerPriority;
            PlanSettings.TryRead(s, cell1, out PlanSettings.Clip clipA, out _);
            bool pastedBefore = clipA != null && PlanHistory.ApplySettings(s, cell2, clipA, out _, out _) && RecipeOf(s, f2) == "alloy_ore" && f2.PowerPriority == 2;
            bool setB = ProductionService.TrySetRecipe(s, f1.BuildingId, "alloy_scrap", out _) && BuildingOps.TrySetPriority(s, f1.BuildingId, 3, out _);
            PlanSettings.TryRead(s, cell1, out PlanSettings.Clip clipB, out _);
            Expect(setup && pastedBefore && setB && clipB != null,
                $"R2 准备：两座建成的精炼炉（{f1.BuildingId}、{f2.BuildingId}），夜前改配方 / 优先级、复制设置粘贴都可以（对照组）（{m1}{m2}{m3}）");

            // 夜前把 f1 改回合金矿（不进撤销栈）：夜里 f1、f2 同配方，便于分别验证“复制设置到同类建筑”改到建成的被整次拒绝、只改到虚影照常写。
            bool f1Ore = ProductionService.TrySetRecipe(s, f1.BuildingId, "alloy_ore", out _);
            VisualElement powerRoot = FgProductionSelfCheck.MountUxml(UiKitFolder + "PowerPanel.uxml", out GameObject powerGo);
            VisualElement prodRoot = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject prodGo);
            PowerPanelUIToolkit.InWorldOverrideForTests = true;
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
            PowerPanelUIToolkit power = powerGo.AddComponent<PowerPanelUIToolkit>();
            power.BindView(powerRoot);
            ProductionPanelUIToolkit prod = prodGo.AddComponent<ProductionPanelUIToolkit>();
            prod.BindView(prodRoot);
            // 电网面板（Alt+G / HUD 电力标签 / 通用面板“电网…”打开的正式入口）夜前可改优先级（对照组：改一次再改回原值）。
            PowerPanelUIToolkit.OpenFor(f1.BuildingId);
            BuildingRecord sel = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingId == power.SelectedBuildingId);
            int selPrio = sel?.PowerPriority ?? 0;
            bool powerBefore = sel != null && (!BuildingOps.HasPriority(sel.BuildingTypeId)
                                               || (power.SetPriority(selPrio == 1 ? 2 : 1) && sel.PowerPriority != selPrio && power.SetPriority(selPrio) && sel.PowerPriority == selPrio));
            Expect(f1Ore && PowerPanelUIToolkit.IsOpen && powerBefore,
                $"R2 准备：电网面板选中 {power.SelectedBuildingId ?? "无"}，夜前经面板改电力优先级可以（对照组，改完改回 {selPrio}）（“{power.MessageText}”）");

            SkipToTick(s, d.StartTick - 1);
            WorldSimulation.StepMany(3);

            // 电网面板改优先级：静默夜期间被拒，写明原因，优先级不变，只拒一次。
            int rejP = SilentNightService.RejectCount;
            bool powerDenied = sel != null && !power.SetPriority(selPrio == 1 ? 2 : 1) && power.MessageText.Contains("静默夜") && power.MessageText.Contains("优先级")
                               && sel.PowerPriority == selPrio && SilentNightService.RejectCount == rejP + 1;
            Expect(d.Active && powerDenied,
                $"R2 静默夜里经电网面板改电力优先级被拒（“{power.MessageText}”）：{sel?.BuildingId} 优先级仍是 {sel?.PowerPriority}（拒绝 {SilentNightService.RejectCount - rejP} 次）");
            PowerPanelUIToolkit.Close();

            int rej0 = SilentNightService.RejectCount;
            int undo0 = PlanHistory.UndoSteps(s);
            bool paste = !PlanHistory.ApplySettings(s, cell2, clipB, out _, out GridReason pr);
            string pasteText = pr.Describe();
            bool pasteKept = RecipeOf(s, f2) == "alloy_ore" && f2.PowerPriority == 2 && PlanHistory.UndoSteps(s) == undo0;
            PlanStepResult undo = PlanHistory.Undo(s);
            bool undoDenied = !undo.Done && (undo.Text ?? string.Empty).Contains("静默夜") && RecipeOf(s, f2) == "alloy_ore" && f2.PowerPriority == 2 && PlanHistory.UndoSteps(s) == undo0;
            int rejected = SilentNightService.RejectCount - rej0;
            Expect(d.Active && paste && pasteText.Contains("静默夜") && pasteText.Contains("优先级") && pasteKept && undoDenied && rejected == 2,
                $"R2 静默夜里复制设置粘贴到建成的建筑被整次拒绝（“{pasteText}”）：配方、电力优先级都不变、不记撤销；撤销那一步复制设置同样被拒（“{undo.Text}”），这一步留在撤销栈里（拒绝 {rejected} 次）");

            // 粘贴蓝图 / 吸管放下（规划）：虚影带配方与优先级，不弹拒绝；施工单等结束后开工。
            int rej1 = SilentNightService.RejectCount;
            int cues1 = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied);
            GridCell? c3 = FgProductionSelfCheck.FindFree(s, Furnace, 20f, 24f);
            GridOpResult placed = c3.HasValue ? PlanHistory.Place(s, Furnace, c3.Value, 0, clipA.S0, clipA.S1, clipA.S2) : default;
            BuildingRecord ghost = placed.Success ? HomeGridService.FindBuilding(s, placed.BuildingId) : null;
            WorldSimulation.StepMany(2);
            WorkOrderRecord go = ghost != null ? BuildOrderOf(s, ghost.BuildingId) : null;
            bool ghostOk = ghost != null && HomeValleyController.IsPlannedGhost(ghost) && RecipeOf(s, ghost) == "alloy_ore" && ghost.PowerPriority == 2
                           && SilentNightService.RejectCount == rej1 && Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied) == cues1
                           && go != null && SilentNightService.IsConstructionHeld(s, go);
            Expect(ghostOk,
                $"R2 静默夜里放带配方的虚影（吸管 / 粘贴蓝图同一入口 PlanHistory.Place → ApplyBuildingSettings）：虚影配方 {RecipeOf(s, ghost) ?? "无"}、优先级 {ghost?.PowerPriority}，" +
                $"没有拒绝音（拒绝 {SilentNightService.RejectCount - rej1} 次）；施工单等结束后开工（{go?.FailureReason ?? "无施工单"}）");

            // “复制设置到同类建筑”（生产面板按钮 → 确认框 → ProductionService.CopySettingsToSameType）：
            // 再放一座废料合金的虚影 g2；以 g2 为源会改到建成的 f1 / f2 → 不弹确认框、整次拒绝（一座都不改、只拒一次、面板写明原因）；
            // 以 f1 为源只会改到虚影 g2（建成的 f2 与虚影 ghost 本来就是合金矿）→ 照常写、不弹拒绝。
            GridCell? c4 = FgProductionSelfCheck.FindFree(s, Furnace, 26f, 30f);
            GridOpResult placed2 = c4.HasValue ? PlanHistory.Place(s, Furnace, c4.Value, 0, clipB.S0, clipB.S1, clipB.S2) : default;
            BuildingRecord g2 = placed2.Success ? HomeGridService.FindBuilding(s, placed2.BuildingId) : null;
            bool g2Scrap = g2 != null && HomeValleyController.IsPlannedGhost(g2) && RecipeOf(s, g2) == "alloy_scrap";
            int rejC = SilentNightService.RejectCount;
            ProductionPanelUIToolkit.Open(g2?.BuildingId);
            prod.AskCopySettings();
            bool noDialog = !UiConfirmDialog.IsOpen;
            string copyPanelMsg = prod.MessageText;
            bool panelDenied = noDialog && copyPanelMsg.Contains("静默夜") && copyPanelMsg.Contains("配方") && SilentNightService.RejectCount == rejC + 1;
            int nDirect = ProductionService.CopySettingsToSameType(s, g2?.BuildingId, out _, out string copyMsg);
            bool directDenied = nDirect == -1 && copyMsg != null && copyMsg.Contains("静默夜") && SilentNightService.RejectCount == rejC + 2;
            bool copyKept = RecipeOf(s, f1) == "alloy_ore" && RecipeOf(s, f2) == "alloy_ore" && RecipeOf(s, ghost) == "alloy_ore";
            if (UiConfirmDialog.IsOpen)
            {
                UiConfirmDialog.Cancel();
            }
            Expect(g2Scrap && panelDenied && directDenied && copyKept,
                $"R2 静默夜里“复制设置到同类建筑”要改到建成的建筑时整次拒绝：面板不弹确认框（{noDialog}）、写明原因“{copyPanelMsg}”；服务入口返回 {nDirect}（“{copyMsg}”）；" +
                $"f1 / f2 / 虚影配方都不变（{RecipeOf(s, f1)} / {RecipeOf(s, f2)} / {RecipeOf(s, ghost)}），每次只拒一次（共 {SilentNightService.RejectCount - rejC} 次）");
            int rejG = SilentNightService.RejectCount;
            int nGhost = ProductionService.CopySettingsToSameType(s, f1.BuildingId, out int sameN, out string ghostCopyMsg);
            Expect(nGhost == 1 && RecipeOf(s, g2) == "alloy_ore" && SilentNightService.RejectCount == rejG,
                $"R2 静默夜里“复制设置到同类建筑”只改到虚影（新的施工规划）时照常写：改了 {nGhost} 座、本来相同 {sameN} 座，虚影 g2 配方 {RecipeOf(s, g2)}，没有拒绝（“{ghostCopyMsg}”）");

            // 刻录目标就是刻录台的配方：玩家入口同样被拒（锁在最前，先于“找不到建筑”）。
            int rejB = SilentNightService.RejectCount;
            bool burnDenied = !ProductionService.TrySetBurnTarget(s, f1.BuildingId, null, out string burnMsg) && burnMsg != null && burnMsg.Contains("静默夜")
                              && SilentNightService.RejectCount == rejB + 1;
            Expect(burnDenied, $"R2 静默夜里改刻录目标被拒并写明原因（“{burnMsg}”）");

            // 结束后：虚影配方仍与蓝图一致；撤销恢复可用（先撤销两次放虚影，再撤销复制设置 → f2 回到废料合金与原优先级）。
            SkipToTick(s, d.EndTick - 1);
            WorldSimulation.StepMany(3);
            string ghostRecipe = RecipeOf(s, ghost) ?? "无";
            bool ghostKept = ghost != null && ghostRecipe == "alloy_ore" && go != null && !SilentNightService.IsConstructionHeld(s, go);
            PlanStepResult u0 = PlanHistory.Undo(s);
            PlanStepResult u1 = PlanHistory.Undo(s);
            PlanStepResult u2 = PlanHistory.Undo(s);
            Expect(!d.Active && ghostKept && u0.Done && u1.Done && u2.Done && RecipeOf(s, f2) == "alloy_scrap" && f2.PowerPriority == prio2,
                $"R2 静默夜结束后：虚影配方仍是蓝图里的 {ghostRecipe}、施工放行；留在撤销栈里的那一步复制设置照常撤销（先撤销两次放虚影，再撤销复制设置：f2 回到 {RecipeOf(s, f2)} / 优先级 {f2.PowerPriority}）");
            }
            finally
            {
                PowerPanelUIToolkit.Close();
                ProductionPanelUIToolkit.Close();
                PowerPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(powerGo);
                Object.DestroyImmediate(prodGo);
            }
        }

        // ── X1 FGT-ENV-003 远征准备跨静默夜提示 ───────────────────────────────────

        private static void CheckExpeditionHint()
        {
            CampaignState s = NewWorld(7221);
            SkipTo(s, 4, 9.0);
            WorldSimulation.StepMany(1);
            string far = SilentNightService.ExpeditionCrossText(s);
            SkipTo(s, 5, 12.0);
            WorldSimulation.StepMany(1);
            string near = SilentNightService.ExpeditionCrossText(s);
            SkipToTick(s, D(s).StartTick + Hz);
            WorldSimulation.StepMany(2);
            string during = SilentNightService.ExpeditionCrossText(s);
            Expect(far == null && near != null && near.Contains("第 5 日 22:00") && near.Contains("安全模式") && during != null && D(s).Active
                   && during.Contains("正在进行") && during.Contains("02:00") && !during.Contains("22:00") && !during.Contains("下一个"),
                $"X1 FGT-ENV-003 远征准备提示：第 4 日上午（离静默夜超过估计的 {SilentNightService.ExpeditionEstimateHours:0} 游戏小时）不提示；第 5 日中午提示“{near}”；" +
                $"静默夜进行中改说正在进行、到几点结束（不再写已经过去的开始时刻）：“{during}”");
        }

        // ── B1 风暴残骸按种子（B25）────────────────────────────────────────────────

        private static string RunToEndAndListDebris(int seed)
        {
            CampaignState s = NewWorld(seed);
            SilentNightState d = D(s);
            SkipToTick(s, d.StartTick - 1);
            WorldSimulation.StepMany(3);
            SkipToTick(s, d.EndTick - 1);
            WorldSimulation.StepMany(3);
            if (d.Active)
            {
                return "没结束";
            }
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            float rMin = GridContent.Tuning("env.silent.debris_ring_min");
            float rMax = GridContent.Tuning("env.silent.debris_ring_max");
            var parts = new List<string>();
            foreach (string id in d.LastDebrisIds)
            {
                GroundItemRecord g = HomeValleyCargo.FindGroundItem(s, id);
                if (g == null)
                {
                    return "缺地面物 " + id;
                }
                var cell = new GridCell(Mathf.RoundToInt(g.Position.x), Mathf.RoundToInt(g.Position.y));
                float dist = Vector2.Distance(g.Position, new Vector2(core.X, core.Y));
                if (!WorldGenQuery.IsWalkableGenerated(s, cell.X, cell.Y) || !map.IsExplored(cell) || dist < rMin - 1.5f || dist > rMax + 1.5f)
                {
                    return $"位置不合法 {cell.X},{cell.Y}（距核心 {dist:0.#}）";
                }
                parts.Add($"{g.ResourceType}×{g.Amount}@{cell.X - core.X},{cell.Y - core.Y}");
            }
            return parts.Count == 0 ? "空" : string.Join(" ", parts);
        }

        private static void CheckDebrisSeeds()
        {
            var shown = new List<string>();
            bool valid = true;
            string first = null;
            foreach (int seed in new[] { 7231, 7232, 7233 })
            {
                string r = RunToEndAndListDebris(seed);
                first ??= r;
                valid &= !r.StartsWith("缺", StringComparison.Ordinal) && !r.StartsWith("位置", StringComparison.Ordinal) && r != "没结束" && r != "空";
                shown.Add($"种子 {seed}：{r}");
            }
            string again = RunToEndAndListDebris(7231);
            Expect(valid && again == first,
                "B1 风暴残骸（B25：按种子地形找空地，不写死坐标）：三个种子都放在核心外 ring 范围的可走、已探索空地上；同一种子重跑结果相同——" + string.Join("；", shown));
        }

        // ── T1 / T2 暂停与倍速、观察一致 ─────────────────────────────────────────

        private const int SegA = 80; // 20:46 起 80 游戏分钟：预警（20:48）→ 静默夜预案（21:59:30）→ 开始（22:00）→ 22:06

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld, out int frames)
        {
            CampaignState s = NewWorld(seed, observe);
            SilentNightState d = D(s);
            pausedHeld = true;
            frames = 0;
            StandingRuleService.TryCreateFromPreset(s, "preset.silent_default", out StandingRuleRecord rule, out _);
            if (rule != null)
            {
                StandingRuleService.TryAddMachine(s, rule.Serial, HomeMachine(HomeValleyLayout.Erc002ChassisId), out _);
            }
            if (rule != null && !rule.Enabled)
            {
                StandingRuleService.TrySetEnabled(s, rule.Serial, true, out _);
            }
            PlaceGhost(s);
            SkipTo(s, 5, 20.0 + 46.0 / 60.0);
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
                pausedHeld = JsonUtility.ToJson(s) == p0 && GameClock.Ticks == t0 && !d.Warned;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.TicksFor(SegA * GameClock.HourSeconds / 60.0);
            while (GameClock.Ticks < target && frames < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            // 第二段：跳到结束前 2 游戏分钟，再跑 4 游戏分钟（跨过结束：风暴残骸、总结、暂缓施工放行）。
            SkipToTick(s, d.EndTick - GameClock.TicksFor(2 * GameClock.HourSeconds / 60.0));
            long target2 = GameClock.Ticks + GameClock.TicksFor(4 * GameClock.HourSeconds / 60.0);
            int guard = 0;
            while (GameClock.Ticks < target2 && guard++ < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target2);
            }
            GameClock.SetSpeed(1f);
            WorldSimulation.SyncAllForSave();
            return Masked(JsonUtility.ToJson(s)) + "|silent=" + JsonUtility.ToJson(d) + "|count=" + d.Count + "|warned=" + SilentNightService.WarnCount;
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
            long steps = GameClock.TicksFor(SegA * GameClock.HourSeconds / 60.0);
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(7241, true, speed, true, out bool held, out int frames);
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
            Expect(paused && same && proportional && reference.Contains("\"Count\":1") && reference.Contains("\"Active\":false"),
                $"T1 暂停与 0.5x～3x（FG07 第 5 节“3x 下跨过静默夜行为与 1x 一致；预警按游戏时间计算”）：暂停中 120 帧整份战役状态不动、不发预警；{string.Join(" / ", shown)} 跑同样的预警 → 预案 → 开始 → 结束（风暴残骸、总结、暂缓施工放行），" +
                "整份战役状态 JSON 逐字段一致，所需真实帧与倍速成反比" + (diff.Length == 0 ? string.Empty : "\n" + diff));
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(7242, true, 1f, false, out _, out _);
            string unseen = RunScenario(7242, false, 1f, false, out _, out _);
            Expect(seen == unseen, "T2 家园在观察与不观察时跨过整个静默夜（预警、预案、开始、突袭触发、结束、风暴残骸），整份战役状态逐字段一致（D-06 / FGR-BASE-021）" +
                                   (seen == unseen ? string.Empty : "\n" + FirstDiff(seen, unseen)));
        }

        // ── S1 静默夜中真文件存读档 ───────────────────────────────────────────────

        private static string Snap(CampaignState s, string ghost)
        {
            SilentNightState d = D(s);
            WorkOrderRecord b = BuildOrderOf(s, ghost);
            string debris = string.Join(";", (d.LastDebrisIds ?? Array.Empty<string>()).Select(id => HomeValleyCargo.FindGroundItem(s, id)).Where(g => g != null)
                .Select(g => $"{g.ResourceType}x{g.Amount}@{R(g.Position.x)},{R(g.Position.y)}"));
            return $"t={GameClock.Ticks}|silent={JsonUtility.ToJson(d)}|remain={R(SilentNightService.RemainingSeconds(s))}|active={SilentNightService.IsActive(s)}" +
                   $"|plans={RaidDirectorService.Plans(s).Count(p => p.Trigger == RaidCatalog.TriggerSilentNight)}|b={b?.State}/{b?.FailureReason}|debris={debris}";
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(7251);
            SilentNightState d = D(s);
            SkipToTick(s, d.StartTick - 1);
            WorldSimulation.StepMany(3);
            SkipToTick(s, d.StartTick + GameClock.TicksFor(GameClock.HourSeconds));
            WorldSimulation.StepMany(2);
            string ghost = PlaceGhost(s);
            WorldSimulation.StepMany(2);
            string before = Snap(s, ghost);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long until = d.EndTick + 5L * Hz;
            SkipToTick(s, d.EndTick - 2);
            WorldSimulation.StepMany((int)(until - GameClock.Ticks));
            string continuous = Snap(s, ghost);
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
                    Fail("S1 读档失败：" + rr.Message);
                    return;
                }
                CampaignState l = rr.State;
                CampaignSession.Set(Slot, l);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                string ld = Snap(l, ghost);
                bool silentNow = SignalLinkService.IsSilentNight;
                SkipToTick(l, D(l).EndTick - 2);
                WorldSimulation.StepMany((int)(until - GameClock.Ticks));
                string af = Snap(l, ghost);
                if (round == 0)
                {
                    loaded = ld + "|uplinkBlocked=" + silentNow;
                    after = af;
                }
                else if (ld + "|uplinkBlocked=" + silentNow != loaded || af != after)
                {
                    after = "第二次读档不同：" + af;
                }
            }
            Expect(save.Success && loaded == before + "|uplinkBlocked=True" && after == continuous && continuous.Contains("active=False") && before.Contains("active=True"),
                $"S1 存档时处于静默夜中（FG07 第 5 节）：读档后静默夜剩余时间、暂缓施工、突袭计划逐位一致（{before}），接入判定入口仍为真；接着跑过结束（风暴残骸、总结）与不存档一直跑一致；同一存档读两次一致" +
                (loaded == before + "|uplinkBlocked=True" ? string.Empty : $"\n存档前 {before}\n读档后 {loaded}") + (after == continuous ? string.Empty : $"\n一直跑 {continuous}\n读档跑 {after}"));
        }

        // ── N1 负向 ───────────────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            // 时间被整段跳过（只有自检跳时 / 损坏的旧档）：不补发效果，排下一次并告警。
            CampaignState s = NewWorld(7261);
            SilentNightState d = D(s);
            int skipped0 = SilentNightService.SkippedCount;
            int starts0 = SilentNightService.StartCount;
            SkipToTick(s, d.EndTick + 10);
            WorldSimulation.StepMany(1);
            bool skip = SilentNightService.SkippedCount == skipped0 + 1 && SilentNightService.StartCount == starts0 && !d.Active && d.NextDay == 10;
            // 没接正式入口：世界模拟不推进静默夜（自检之间复位后也不会误触发）。
            CampaignState u = NewWorld(7262);
            SilentNightService.ResetForTests();
            SkipToTick(u, D(u).StartTick + 5);
            WorldSimulation.StepMany(2);
            bool idle = !D(u).Active && !SignalLinkService.IsSilentNight && !SilentNightService.IsActiveNow();
            // 难度中途改：已排定的这一次不变，再下一次按新间隔（标准 → 严酷：第 5 日之后是第 9 日）。
            CampaignState h = NewWorld(7263);
            SilentNightState hd = D(h);
            h.DifficultyId = "Harsh";
            int planned = hd.NextDay;
            SkipToTick(h, hd.StartTick - 1);
            WorldSimulation.StepMany(3);
            SkipToTick(h, hd.EndTick - 1);
            WorldSimulation.StepMany(3);
            Expect(skip && idle && planned == 5 && hd.NextDay == 9,
                $"N1 负向：时间整段跳过静默夜 → 不补发效果、告警并排下一次（第 {d.NextDay} 日）{skip}；没接正式入口时世界模拟不推进 {idle}；难度中途改为严酷 → 已排定的第 {planned} 日不变，再下一次第 {hd.NextDay} 日");
        }

        // ── U1 横幅与日历行 ───────────────────────────────────────────────────────

        private static void CheckHud()
        {
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "WorldBar.uxml", out GameObject go);
            try
            {
                var bar = go.AddComponent<WorldBarHudUIToolkit>();
                bar.BindView(root);
                CampaignState s = NewWorld(7271);
                SilentNightState d = D(s);
                SkipTo(s, 5, 21.0);
                WorldSimulation.StepMany(2);
                bar.Refresh();
                string pre = bar.CalendarExtraText;
                string preBanner = bar.SilentBannerText;
                SkipToTick(s, d.StartTick - 1);
                WorldSimulation.StepMany(3);
                SkipToTick(s, d.StartTick + GameClock.TicksFor(GameClock.HourSeconds));
                WorldSimulation.StepMany(2);
                bar.Refresh();
                string banner = bar.SilentBannerText;
                string during = bar.CalendarExtraText;
                GameSettings.SetLanguage(GameLanguage.En);
                bar.Refresh();
                string en = bar.SilentBannerText;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                SkipToTick(s, d.EndTick - 1);
                WorldSimulation.StepMany(3);
                bar.Refresh();
                string afterBanner = bar.SilentBannerText;
                // 布局探针（四种分辨率 + 超长文字 + USS 体检）：横幅显示时世界时间条不溢出、不重叠。
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "WorldBar.uxml", "WorldBar", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("WorldBar")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("WorldSilentBanner")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "U1 世界时间条（横幅显示）布局探针：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
                Expect(pre.Contains("★") && pre.Contains("静默夜") && pre.Contains("后") && preBanner.Length == 0
                       && banner.Contains("信号中断") && banner.Contains("剩余") && banner.Contains("3时") && during.Contains("进行中")
                       && en.Contains("SIGNAL LOST") && afterBanner.Length == 0,
                    $"U1 世界时间条：静默夜前日历行“{pre}”（倒计时）；静默夜中顶部横幅“{banner}”、日历行“{during}”，英文“{en}”；结束后横幅隐藏");
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Object.DestroyImmediate(go);
            }
        }

        // ── U2 暂停菜单压暗滑条 ───────────────────────────────────────────────────

        private static void CheckPauseMenu()
        {
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject go);
            try
            {
                GameSettings.ResetSilentNightDim();
                var menu = go.AddComponent<PauseMenuUIToolkit>();
                menu.BindView(root);
                Slider slider = menu.SilentDimSlider;
                bool range = slider != null && Mathf.Approximately(slider.lowValue, GameSettings.SilentNightDimMin) && Mathf.Approximately(slider.highValue, GameSettings.SilentNightDimMax)
                             && Mathf.Approximately(slider.value, GameSettings.SilentNightDimDefault) && menu.SilentDimLabelText.Contains("30%");
                slider.value = 0.5f;
                bool applied = Mathf.Approximately(GameSettings.SilentNightDim, 0.5f) && menu.SilentDimLabelText.Contains("50%");
                GameSettings.SetSilentNightDim(5f);
                bool clamped = Mathf.Approximately(GameSettings.SilentNightDim, GameSettings.SilentNightDimMax);
                GameSettings.Load();
                bool persisted = Mathf.Approximately(GameSettings.SilentNightDim, GameSettings.SilentNightDimMax);
                Click(menu.SilentDimResetButton);
                UiEscapeStack.Clear();
                menu.SyncSilentDim();
                bool reset = Mathf.Approximately(GameSettings.SilentNightDim, GameSettings.SilentNightDimDefault) && Mathf.Approximately(slider.value, GameSettings.SilentNightDimDefault);
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "PauseMenu.uxml", "PauseMenuWindow");
                Expect(!probe.Contains("FAIL"), "U2 暂停菜单加了“静默夜画面压暗”滑条后布局探针仍通过：" + probe.Split('\n')[0]);
                Expect(range && applied && clamped && persisted && reset,
                    $"U2 暂停菜单“画面”静默夜压暗强度（FG07 第 4 节）：滑条范围 {GameSettings.SilentNightDimMin:P0}～{GameSettings.SilentNightDimMax:P0}、默认 30%，拖动立即生效、越界钳回、重开游戏保留，“恢复默认压暗”复位（{range}/{applied}/{clamped}/{persisted}/{reset}）");
            }
            finally
            {
                GameSettings.ResetSilentNightDim();
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
        }

        private static bool Click(Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        // ── L1 画面压暗 ───────────────────────────────────────────────────────────

        private static void CheckLighting()
        {
            var lightGo = new GameObject("fgsilent-sun");
            try
            {
                Light sun = lightGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1f;
                RenderSettings.sun = sun;
                DayNightLighting.ResetForTests();
                GameSettings.ResetSilentNightDim();
                float dim = GameSettings.SilentNightDim;
                DayNightLighting.Apply(1f, 0f);
                float day = DayNightLighting.Brightness;
                DayNightLighting.Apply(1f, 1f);
                float silentDay = DayNightLighting.Brightness;
                DayNightLighting.Apply(0f, 1f);
                float silentNight = DayNightLighting.Brightness;
                DayNightLighting.Apply(0f);
                float plainNight = DayNightLighting.Brightness;
                float floor = GameSettings.NightBrightnessFloor;
                Expect(Math.Abs(day - 1f) < 1e-4f && Math.Abs(silentDay - (1f - dim)) < 1e-4f && Math.Abs(silentNight - floor * (1f - dim)) < 1e-4f
                       && Math.Abs(plainNight - floor) < 1e-4f && Math.Abs(sun.intensity - floor) < 1e-4f,
                    $"L1 画面压暗（FGR-ENV-012）：亮度 = 昼夜亮度 ×（1 − 压暗强度 {dim:P0} × 淡入系数）——白天 {day:0.##} → 静默夜 {silentDay:0.##}；夜里 {plainNight:0.##} → 静默夜 {silentNight:0.##}；只改场景光照，单参数入口（没有静默夜）不受影响");
            }
            finally
            {
                DayNightLighting.ResetForTests();
                Object.DestroyImmediate(lightGo);
            }
        }

        // ── P1 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7281);
            SilentNightState d = D(s);
            const int n = 200000;
            var sw = Stopwatch.StartNew();
            long t = GameClock.Ticks;
            for (int i = 0; i < n; i++)
            {
                SilentNightService.WorldStep(s, t);
            }
            double idleUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            SkipToTick(s, d.StartTick);
            sw.Restart();
            SilentNightService.WorldStep(s, d.StartTick);
            double startMs = sw.Elapsed.TotalMilliseconds;
            SkipToTick(s, d.EndTick);
            sw.Restart();
            SilentNightService.WorldStep(s, d.EndTick);
            double endMs = sw.Elapsed.TotalMilliseconds;
            PerfLines.Add($"静默夜每步检查 {idleUs:F3} μs / 步，开始那一步 {startMs:F2} ms（断链、远征安全模式、突袭触发、通知），结束那一步 {endMs:F2} ms（{d.LastDebrisIds.Length} 处风暴残骸 + 搬运单 + 总结）");
            PerfGate.Expect(!d.Active && d.Count == 1, $"P1 静默夜每步检查 {idleUs:F3} μs / 步（阈值 0.5 μs；O(1)，与实体数无关）、开始那一步 {startMs:F2} ms（阈值 5 ms）、结束那一步 {endMs:F2} ms（阈值 10 ms）",
                new[] { PerfGate.Le(idleUs, 0.5, "静默夜每步 μs"), PerfGate.Le(startMs, 5.0, "开始 ms"), PerfGate.Le(endMs, 10.0, "结束 ms") }, Expect, Line);
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
