using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG9-BAL-01 第一幕平衡（FG16 FGR-BAL-060～062；验收 FGT-BAL-004 的第一幕部分）的自检——起真实世界跑，行为坏掉就会失败：
    /// A 数据：节奏机器人玩家模型与第一次静默夜推迟（env.silent.first_extra_days）在表里、三档难度的第一次静默夜日期 = 间隔 + 推迟。
    /// B FGT-BAL-004：第一幕节奏机器人（加速模拟）在测试种子集 1 / 42 / 20261017 上各从新游戏走到第一幕结束（标准难度）：
    ///   真实世界逐步推进（统一时钟、突袭导演、静默夜、领地增援、节奏台账都按正式规则走），机器人在节奏点之间按表里的玩家模型（bal.bot.*）等待，
    ///   再走真实入口（修好发电机、造履带机、进入 / 撤离地点、真实返程行军、建炮塔 / 超控阵列、击败首领）；出征行军时长 = 真实路线 ÷ 真实行军速度（按种子不同）。
    ///   每个节奏点（含真实突袭导演给出的第一次突袭、真实静默夜）到达时刻与 fg.TbActPacing 目标的偏差 ≤ 25%，核心不丢。
    /// C 负向：严酷 / 建造者难度各跑一遍（种子 42）——第一幕照样走完、核心不丢、主线点全部到达；严酷静默夜更早、首领战更久；建造者没有非剧情突袭、静默夜更晚。
    ///   旧规则（第一次静默夜不推迟）下第一次静默夜偏差超 25%（检测器真的会拦，BAL-REC-002 的依据）。
    /// D 存读档：机器人走完第一幕后存档、读档，节奏台账逐条一致（时刻不变、不重复记）。
    /// E 突袭强度 vs 第一套防线（FG-GAP-146 的数据）：机器人家园的真实突袭结算（等级、展开 / 击毁、建筑损失）记进报告，核心从不丢失。
    /// F 性能（Editor batchmode，一次测量）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgAct1BalanceSelfCheck）；单段：bash tools/unity-validate.sh --segment FgAct1BalanceSelfCheck。
    /// </summary>
    public static class FgAct1BalanceSelfCheck
    {
        private const int Slot = 7;
        private const int RunSlot = 8;
        private const string City = FracturedCityLayout.RegionId;
        private const string Fo = FoundryOutpostLayout.RegionId;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static readonly int[] Seeds = { 1, 42, 20261017 };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq;

        [MenuItem("BinGames/QA/自检/FG 第一幕平衡与节奏机器人")]
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
            _std42 = null; // 复审 P2：C 段不读上一次 Run 留下的标准难度结果
            Line("\n[第一幕平衡与节奏机器人] FGT-BAL-004 第一幕部分：3 个测试种子 × 标准难度、严酷 / 建造者负向、存读档、突袭 vs 第一套防线（FG9-BAL-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            bool legacy = ResearchGate.LegacyGatesOpenForTests;
            bool legacyJourneys = Act1PacingService.LegacyGatesOpenForJourneys;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgbal-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                BuildingOps.Reload();
                NarrativeCatalog.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex_discovered.json");
                MechanicCodex.Reload();
                GameRoot.BindWorldProviders();
                // 与 GameRoot.Startup 同一组世界服务：昼夜、静默夜、天气、净化区（节奏机器人要真实的静默夜与天气）；收尾复位。
                RaidDirectorService.EnsureArrivalAutoPauseRegistered();
                DayNightCycle.Install();
                SilentNightService.Install();
                WeatherService.Install();
                PurificationService.Install();
                NarrativeService.EnsureListening();
                Act1PacingService.EnsureListening();
                SiteGenLayout.ResetCache();
                // 节奏机器人按正式节奏走（节奏门槛、研发门槛都不开旧规则开关）。
                ResearchGate.LegacyGatesOpenForTests = false;
                Act1PacingService.LegacyGatesOpenForJourneys = false;

                Step(CheckData);
                Step(CheckFirstSilentBudget);
                Step(CheckSeedsStandard);
                Step(CheckHarshAndBuilder);
                Step(CheckOldSilentRuleFails);
            }
            finally
            {
                SilentNightService.ResetForTests();
                WeatherService.ResetForTests();
                DayNightCycle.ResetForTests();
                PurificationService.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = null;
                MechanicCodex.Reload();
                ResearchGate.LegacyGatesOpenForTests = legacy;
                Act1PacingService.LegacyGatesOpenForJourneys = legacyJourneys;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                SiteGenLayout.ResetCache();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
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
                if (originalSession != null && originalSlot >= 0)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  第一幕平衡与节奏机器人：全部 {_pass} 项通过" : $"  第一幕平衡与节奏机器人：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ───────────────────────────── A 数据 ─────────────────────────────

        private static readonly string[] BotKeys =
        {
            "bal.bot.power_minutes", "bal.bot.machine_minutes", "bal.bot.expedition_prep_minutes", "bal.bot.team_size", "bal.bot.uplink_minutes", "bal.bot.city_stay_minutes",
            "bal.bot.turret_minutes", "bal.bot.turrets_extra", "bal.bot.foundry_prep_minutes", "bal.bot.foundry_stay_minutes", "bal.bot.silent_prep_minutes",
            "bal.bot.leader_fight_minutes", "bal.bot.override_minutes", "bal.bot.core_prep_minutes", "bal.bot.core_fight_minutes", "bal.bot.max_deviation", "bal.bot.repair_minutes", "bal.bot.turrets_max", "bal.bot.exposure_off_at", "bal.bot.exposure_on_at",
        };

        private static void CheckData()
        {
            var missing = BotKeys.Where(k => !GridContent.TryGetTuning(k, out _)).ToList();
            bool extraRow = GridContent.TryGetTuning("env.silent.first_extra_days", out float extra);
            var days = new List<string>();
            bool firstOk = true;
            foreach ((string diff, int interval) in new[] { ("Standard", 5), ("Builder", 7), ("Harsh", 4) })
            {
                CampaignState s = CampaignState.CreateNew("fgbal-data-" + diff, diff, 1);
                int first = SilentNightService.FirstDay(s);
                firstOk &= SilentNightService.IntervalDays(s) == interval && first == interval + (int)extra;
                days.Add($"{diff} 间隔 {SilentNightService.IntervalDays(s)} 日、第一次第 {first} 日");
            }
            string line = GameText.Format("difficulty.line.silent_night", 5, 7);
            Expect(missing.Count == 0 && extraRow && extra >= 1f && firstOk && line.Contains("7"),
                $"A1 表：节奏机器人玩家模型 {BotKeys.Length} 个键都在 fg.TbHomeTuning（缺 {string.Join(",", missing)}）；第一次静默夜推迟 {extra} 日：{string.Join("；", days)}；难度说明写出第一次的日期（“{line}”）");
        }

        /// <summary>
        /// A2 BAL-REC-003 双分支（复审 P1 / P2）：静默夜突袭只在“还没有任何突袭真正到达过”时缩小——
        /// 晚到的普通首袭已经排定（计划抵达步已写）但还没到达，静默夜突袭仍缩小；有一波真正到达之后不再缩小；非静默夜触发不受影响。
        /// </summary>
        private static void CheckFirstSilentBudget()
        {
            CampaignState s = CampaignState.CreateNew("fgbal-budget", "Standard", 1);
            var d = new RaidDirectorState { LastAnyArrivalTick = GameClock.TicksFor(4 * 3600.0), LastArrivalTick = GameClock.TicksFor(4 * 3600.0) };
            var silent = new RaidPlanRecord { Level = 1, Trigger = RaidCatalog.TriggerSilentNight, CreatedTick = GameClock.TicksFor(2 * 3600.0) };
            var exposure = new RaidPlanRecord { Level = 1, Trigger = "exposure", CreatedTick = silent.CreatedTick };
            double raw = RaidCatalog.LevelBudget(1) * RaidCatalog.ActCoef(1) * DifficultyService.Scale(s) * RaidDirectorService.IntervalCoef(d, silent.CreatedTick);
            int small = Math.Max(1, (int)Math.Round(raw * RaidCatalog.FirstRaidScale));
            int full = Math.Max(1, (int)Math.Round(raw));
            int beforeArrival = RaidDirectorService.ComputeBudget(s, d, silent);
            int normal = RaidDirectorService.ComputeBudget(s, d, exposure);
            d.ArrivedWaveCount = 1;
            int afterArrival = RaidDirectorService.ComputeBudget(s, d, silent);
            Expect(small < full && beforeArrival == small && afterArrival == full && normal == full,
                $"A2 BAL-REC-003：普通首袭已排定（计划抵达第 {Min(d.LastAnyArrivalTick):F0} 分）但没到达时静默夜突袭预算 {beforeArrival}（应缩小为 {small}）；" +
                $"有一波真正到达后 {afterArrival}（应为 {full}）；非静默夜、非首袭的普通触发 {normal}（应为 {full}）");
        }

        // ───────────────────────────── B FGT-BAL-004 ─────────────────────────────

        private static BotRun _std42;

        private static void CheckSeedsStandard()
        {
            float maxDev = T("bal.bot.max_deviation");
            var all = new List<BotRun>();
            foreach (int seed in Seeds)
            {
                BotRun run = RunBot(seed, "Standard", saveCheck: seed == 42);
                all.Add(run);
                if (seed == 42)
                {
                    _std42 = run;
                }
                Line($"  - 种子 {seed} 标准：{run.Table()}（真实耗时 {run.WallSeconds:F0} 秒、世界步 {run.Steps}）；突袭计划 [{string.Join("；", run.PlanLines)}]");
                List<string> off = run.Deviations().Where(d => !d.Ok(maxDev)).Select(d => d.Text).ToList();
                bool allReached = Act1PacingService.AllRows().All(r => run.Minutes.ContainsKey(r.Id));
                Expect(run.Error == null && allReached && off.Count == 0 && !run.CoreLost && run.Act == 2,
                    $"B{Array.IndexOf(Seeds, seed) + 1} FGT-BAL-004 种子 {seed}：第一幕 {Act1PacingService.AllRows().Count} 个节奏点全部到达 {allReached}、每个点与目标偏差 ≤ {maxDev:P0}（超出：{string.Join("；", off)}）、核心不丢 {!run.CoreLost}、进入第 {run.Act} 幕{(run.Error == null ? string.Empty : "；出错：" + run.Error)}");
                Expect(run.RaidLines.Count > 0 && run.RaidLines.All(r => !r.Contains("core_lost")),
                    $"E{Array.IndexOf(Seeds, seed) + 1} 突袭 vs 第一套防线（种子 {seed}，第一座炮塔起每次突袭预警后补 {(int)T("bal.bot.turrets_extra")} 座、至多 {(int)T("bal.bot.turrets_max")} 座（× 难度突袭规模），FG-GAP-146）：第一幕里真实突袭 {run.RaidLines.Count} 次、核心从未丢失（每次突袭后修好受损建筑 {run.Repairs} 座次；真实花废料 {run.ScrapSpent}、经济缺口 {run.ScrapGap}（机器人不玩家园经济，DEBT-FG9BAL01-02）[{string.Join("；", run.GapLines)}]；暴露 ≥ {T("bal.bot.exposure_off_at"):F0} 关广播 {run.BroadcastToggles} 次）；结算 [{string.Join("；", run.RaidLines)}]");
                if (run.SaveLine != null)
                {
                    Expect(run.SaveOk, "D1 存读档：" + run.SaveLine);
                }
            }
            // 种子之间的差别来自真实路线：铸造前哨 / 破碎都市行军时长随种子不同。
            string marches = string.Join("；", all.Select(r => $"种子 {r.Seed} 去破碎都市 {r.MarchCity:F1} 分 / 铸造前哨 {r.MarchFo:F1} 分"));
            Expect(all.All(r => r.MarchCity > 0 && r.MarchFo > r.MarchCity) && all.Select(r => r.MarchFo).Distinct().Count() > 1,
                $"B4 行军时长取真实路线（按种子不同）：{marches}");
            double perRun = all.Average(r => r.WallSeconds);
            Line($"  - 性能（Editor batchmode，一次测量；真机不跑本机器人）：每跑完一次第一幕（约 {all[0].Steps} 个世界步）平均 {perRun:F0} 秒");
            Expect(perRun < 300.0, $"F1 节奏机器人每次 < 300 秒（实测 {perRun:F0}；它逐步推进 8 个 1x 小时的真实世界）");
        }

        // ───────────────────────────── C 负向：难度 ─────────────────────────────

        private static void CheckHarshAndBuilder()
        {
            float maxDev = T("bal.bot.max_deviation");
            BotRun std = _std42 ?? RunBot(42, "Standard", saveCheck: false);
            BotRun harsh = RunBot(42, "Harsh", saveCheck: false);
            BotRun builder = RunBot(42, "Builder", saveCheck: false);
            foreach (BotRun r in new[] { harsh, builder })
            {
                Line($"  - 种子 42 {r.Difficulty}：{r.Table()}；突袭 [{string.Join("；", r.RaidLines)}]；计划 [{string.Join("；", r.PlanLines)}]");
            }
            string[] mainline = Act1PacingService.AllRows().Where(r => r.Mainline == 1).Select(r => r.Id).ToArray();
            bool harshMain = mainline.All(harsh.Minutes.ContainsKey);
            bool builderMain = mainline.All(builder.Minutes.ContainsKey);
            double M(BotRun r, string id) => r.Minutes.TryGetValue(id, out double v) ? v : -1;
            bool harshSilentEarlier = M(harsh, "first_silent_night") > 0 && M(harsh, "first_silent_night") < M(std, "first_silent_night");
            bool harshFightsLonger = M(harsh, "foundry_core") > M(std, "foundry_core");
            bool builderSilentLater = M(builder, "first_silent_night") > M(std, "first_silent_night");
            bool builderStoryOnly = builder.RaidLines.All(l => l.Contains(" 级 " + RaidCatalog.TriggerStory + " "));
            List<string> harshOff = harsh.Deviations().Where(d => !d.Ok(maxDev)).Select(d => d.Text).ToList();
            List<string> builderOff = builder.Deviations().Where(d => !d.Ok(maxDev)).Select(d => d.Text).ToList();
            Expect(harsh.Error == null && harshMain && !harsh.CoreLost && harsh.Act == 2 && harshSilentEarlier,
                $"C1 严酷（种子 42）：第一幕走完、主线点全部到达 {harshMain}、核心不丢 {!harsh.CoreLost}；静默夜更早 {M(harsh, "first_silent_night"):F0} < 标准 {M(std, "first_silent_night"):F0} 分；" +
                $"首领战用时（机器人参数 × 敌人耐久倍率，只记录不判，复审 P2）{harshFightsLonger}（铸造主核心 {M(harsh, "foundry_core"):F0} 对 {M(std, "foundry_core"):F0} 分）；真实花废料 {harsh.ScrapSpent}、经济缺口 {harsh.ScrapGap}；超 25% 的点（只记录，严酷难度 FG16 不要求同一节奏）：{string.Join("；", harshOff)}{(harsh.Error == null ? string.Empty : "；出错：" + harsh.Error)}");
            Expect(builder.Error == null && builderMain && !builder.CoreLost && builder.Act == 2 && builderSilentLater && builderStoryOnly,
                $"C2 建造者（种子 42）：第一幕走完、主线点全部到达 {builderMain}、核心不丢 {!builder.CoreLost}；静默夜更晚 {M(builder, "first_silent_night"):F0} > {M(std, "first_silent_night"):F0} 分；只有剧情突袭 {builderStoryOnly}（{builder.RaidLines.Count} 次）；" +
                $"超 25% 的点（只记录）：{string.Join("；", builderOff)}{(builder.Error == null ? string.Empty : "；出错：" + builder.Error)}");
        }

        private static void CheckOldSilentRuleFails()
        {
            // 旧规则（第一次静默夜就在第“间隔”日）：用正式的 PlanTicks 算标准难度第 5 日那一次的开始时刻，偏差超出 25%——BAL-REC-002 的依据，也证明 B 段的检测器会拦。
            CampaignState s = CampaignState.CreateNew("fgbal-old", "Standard", 1);
            float maxDev = T("bal.bot.max_deviation");
            ActPacing row = Act1PacingService.Row("first_silent_night");
            SilentNightService.PlanTicks(SilentNightService.IntervalDays(s), out _, out long oldStart, out _);
            SilentNightService.PlanTicks(SilentNightService.FirstDay(s), out _, out long newStart, out _);
            double oldMin = oldStart / (double)GameClock.StepHz / 60.0;
            double newMin = newStart / (double)GameClock.StepHz / 60.0;
            var oldDev = new PaceDev("first_silent_night", oldMin, row.Minute);
            var newDev = new PaceDev("first_silent_night", newMin, row.Minute);
            Expect(row != null && !oldDev.Ok(maxDev) && newDev.Ok(maxDev),
                $"C3 负向：旧规则第一次静默夜在第 {SilentNightService.IntervalDays(s)} 日 = {oldDev.Text}（超 {maxDev:P0}，会被拦）；推迟 {SilentNightService.FirstExtraDays} 日后第 {SilentNightService.FirstDay(s)} 日 = {newDev.Text}");
        }

        // ───────────────────────────── 节奏机器人 ─────────────────────────────

        private readonly struct PaceDev
        {
            public readonly string Id;
            public readonly double Actual;
            public readonly double Target;

            public PaceDev(string id, double actual, double target)
            {
                Id = id;
                Actual = actual;
                Target = target;
            }

            /// <summary>目标 0（开局）的点按绝对 1 分钟判；其余按相对偏差。</summary>
            public bool Ok(float max) => Target <= 0 ? Math.Abs(Actual - Target) <= 1.0 : Math.Abs(Actual - Target) / Target <= max;

            public string Text => Target <= 0
                ? $"{Id} {Actual:F1} 分（目标 0）"
                : $"{Id} {Actual:F1} 分（目标 {Target:F0}，偏差 {(Actual - Target) / Target:+0%;-0%;0%}）";
        }

        private sealed class BotRun
        {
            public int Seed;
            public string Difficulty;
            public readonly Dictionary<string, double> Minutes = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly List<string> RaidLines = new List<string>();
            public readonly List<string> PlanLines = new List<string>();
            public long CoreHitTick = -1;
            public int ResultsSeen;
            public long RepairAt = -1;
            public int Repairs;
            public int TurretsOwed;
            public readonly List<long> PendingTurrets = new List<long>();
            public int ScrapSpent;
            public int ScrapGap;
            public readonly List<string> GapLines = new List<string>();
            public bool BroadcastOff;
            public int BroadcastToggles;
            public readonly HashSet<string> SeenPlans = new HashSet<string>(StringComparer.Ordinal);
            public double MarchCity;
            public double MarchFo;
            public bool CoreLost;
            public int Act;
            public string Error;
            public double WallSeconds;
            public long Steps;
            public string SaveLine;
            public bool SaveOk;

            public IEnumerable<PaceDev> Deviations()
            {
                foreach (ActPacing r in Act1PacingService.AllRows())
                {
                    if (Minutes.TryGetValue(r.Id, out double m))
                    {
                        yield return new PaceDev(r.Id, m, r.Minute);
                    }
                }
            }

            public string Table() => string.Join("、", Deviations().Select(d => d.Text)) +
                                     (Act1PacingService.AllRows().Any(r => !Minutes.ContainsKey(r.Id)) ? "；没到：" + string.Join(",", Act1PacingService.AllRows().Where(r => !Minutes.ContainsKey(r.Id)).Select(r => r.Id)) : string.Empty);
        }

        /// <summary>
        /// 加速模拟机器人走一次第一幕：真实世界（家园内核、突袭导演、静默夜、领地增援、节奏台账）逐步推进；机器人在节奏点之间按玩家模型等待，然后走真实入口。
        /// 机器人只做 FG16 第 2 节时间线上的那件事（不提前、不多做）；突袭来了靠它建的炮塔守（第一套防线），不手动操作。
        /// </summary>
        private static BotRun RunBot(int seed, string difficulty, bool saveCheck)
        {
            var run = new BotRun { Seed = seed, Difficulty = difficulty };
            var sw = Stopwatch.StartNew();
            long t0 = 0;
            CampaignState s = null;
            try
            {
                s = NewWorld(seed, difficulty);
                t0 = GameClock.Ticks;
                int teamSize = Math.Max(1, (int)T("bal.bot.team_size"));
                // 0:10 修好开局受损的仓库、发电机、信号塔（FGJ-M9A 开局同样三座；与 FgAct1SelfCheck B2 同一个存档事实）。
                // 复审 P1：修复按真实修复表付废料、等真实修复时长。
                Wait(run, s, T("bal.bot.power_minutes"));
                float repairSecs = 0f;
                foreach (BuildingRecord b in s.BuildingRecords.Where(b => b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator
                             || b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse || b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower)).ToList())
                {
                    (int cost, float secs) = RepairCost(b.BuildingTypeId);
                    Spend(run, s, cost, "修复开局 " + b.BuildingTypeId);
                    repairSecs = Math.Max(repairSecs, secs);
                    b.ConstructionState = BuildingConstructionState.Operational;
                    b.Health = BuildingOps.MaxDurability(b.BuildingTypeId);
                }
                Wait(run, s, repairSecs / 60.0);
                HomeValleyPowerGrid.Recompute(s);
                // 0:20 造出第一台战斗履带机：付装配站真实生产价（FactoryProduceDefaults 废料 + 工期）。
                Wait(run, s, T("bal.bot.machine_minutes"));
                HomeValleyLayout.ProduceBlueprintDefault mdef = HomeValleyLayout.FactoryProduceDefaults[HomeValleyLayout.BlueprintErc003Id];
                Spend(run, s, mdef.ScrapCost, "生产履带机");
                Wait(run, s, mdef.Seconds / 60.0);
                var team = new List<int> { Spawn(s, new Vector2(4f, 4f)) };
                team.AddRange(s.MachineRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && !team.Contains(m.LogicId)).Select(m => m.LogicId).Take(teamSize - 1));
                // 0:40 第一次远征：真实路线 ÷ 真实行军速度。
                Wait(run, s, T("bal.bot.expedition_prep_minutes"));
                ExpeditionSiteAnchors.EnsureResolved(s);
                run.MarchCity = MarchMinutes(s, City, team);
                run.MarchFo = MarchMinutes(s, Fo, team);
                Wait(run, s, run.MarchCity);
                // 停留期间地点一直载入（队伍在地点 = 地点在运行，ExpeditionMarchService.Reconcile）；机器人不主动打驻军（驻军战由 FgAct1SelfCheck C 段覆盖）。
                FracturedCityController city = EnterCity(s, team.ToArray());
                // 1:00 第一次接入（在地点里接入一台带接入口的机器——本局事件钩子）。
                Wait(run, s, T("bal.bot.uplink_minutes"));
                GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink);
                Wait(run, s, Math.Max(0.0, T("bal.bot.city_stay_minutes") - T("bal.bot.uplink_minutes")));
                // 撤离 → 真实返程行军 → 到家结算 → 1:30 基础传送带开放。
                city.Exit(true);
                ReturnHome(run, s, City, team);
                // 2:00 第一座炮塔（第一套防线）；第一次突袭由真实突袭导演按暴露 / 宽限期排。
                Wait(run, s, T("bal.bot.turret_minutes"));
                Build(run, s, TurretCatalog.LightTypeId, playerGates: true);
                // 4:00 铸造前哨外围。
                Wait(run, s, T("bal.bot.foundry_prep_minutes"));
                Wait(run, s, run.MarchFo);
                FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
                RegionRecord fr = FoundryOutpostRegion.Find(s);
                fr.State = RegionState.Available;
                fr.ExpeditionCount = Math.Max(1, fr.ExpeditionCount);
                foreach (int m in team.Where(id => Rec(id) != null && Rec(id).IsAlive))
                {
                    MachineRegistry.MoveToRegion(Rec(m), Fo);
                }
                FoundryOutpostController fo = WorldSimulation.LoadFoundryOutpost(team.Where(id => Rec(id) != null && Rec(id).IsAlive).ToArray(), resume: false);
                SetMarch(s, team.Where(id => Rec(id) != null && Rec(id).IsAlive).ToArray(), ExpeditionMarchService.PhaseAtSite);
                Wait(run, s, T("bal.bot.foundry_stay_minutes"));
                fo.Exit(true);
                ReturnHome(run, s, Fo, team);
                // 5:00 寂听主脑（静默领地的室内场地；往返按去破碎都市的真实路线算）。首领战用时乘难度的敌人耐久倍率。
                float hp = DifficultyService.EnemyHealthMul(s);
                Wait(run, s, T("bal.bot.silent_prep_minutes") + run.MarchCity + T("bal.bot.leader_fight_minutes") * hp);
                TerritoryControlService.MarkLeaderDefeated(s, "silent");
                Wait(run, s, run.MarchCity);
                // 6:00 超控阵列 T1。
                Wait(run, s, T("bal.bot.override_minutes"));
                // shortcut: 研发树（signal.override_t1 要实验室出研究点）与监听阵列核没有真实走，只付废料、等工期；承接 DEBT-FG9BAL01-02（FG15-BAL-01）。
                Build(run, s, "override_array", playerGates: false);
                // 7:30 铸造主核心 → 8:00 第一幕结束（收到“它在等什么”）。
                Wait(run, s, T("bal.bot.core_prep_minutes") + run.MarchFo + T("bal.bot.core_fight_minutes") * hp);
                TerritoryControlService.MarkLeaderDefeated(s, "foundry");
                NarrativeService.Trigger(s, "leader:foundry");
                WorldSimulation.StepMany(GameClock.StepHz * 2);
                Collect(run, s, t0);
                if (saveCheck)
                {
                    SaveCheck(run, s);
                }
            }
            catch (Exception e)
            {
                run.Error = e.GetType().Name + "：" + e.Message;
                Debug.LogException(e);
                if (s != null)
                {
                    Collect(run, s, t0);
                }
            }
            run.WallSeconds = sw.Elapsed.TotalSeconds;
            run.Steps = GameClock.Ticks - t0;
            return run;
        }

        private static void Collect(BotRun run, CampaignState s, long t0)
        {
            foreach (ActPaceRecord r in s.Progress?.Pacing ?? Array.Empty<ActPaceRecord>())
            {
                if (r != null)
                {
                    run.Minutes[r.Id] = (r.Tick - t0) / (double)GameClock.StepHz / 60.0;
                }
            }
            run.CoreLost |= RaidResultService.IsCoreLost(s);
            run.Act = s.Progress?.Act ?? 0;
            foreach (RaidResultRecord r in RaidResultService.All(s))
            {
                if (r != null)
                {
                    run.RaidLines.Add($"{r.PlanId} {r.Level} 级 {r.Trigger} {r.Outcome} 击毁 {r.Killed}/{r.Unfolded} 建筑损失 {r.LostBuildings} 第 {(r.EndTick - t0) / (double)GameClock.StepHz / 60.0:F0} 分");
                }
            }
        }

        /// <summary>存档 → 读档：节奏台账逐条一致（时刻不变、不重复记），读档后再推进不改已记的点。</summary>
        private static void SaveCheck(BotRun run, CampaignState s)
        {
            string before = Fingerprint(s);
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(RunSlot), true);
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                run.SaveLine = "读档失败：" + r.Message;
                return;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            Act1PacingService.OnCampaignEntered(r.State);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            string after = Fingerprint(r.State);
            run.SaveOk = saved.Success && before == after && r.State.Progress.Act == 2 && r.State.Progress.Pacing.Length == r.State.Progress.Pacing.Select(p => p.Id).Distinct().Count();
            run.SaveLine = $"种子 {run.Seed} 第一幕走完后存档 {saved.Success}、读档再推进 3 秒：节奏台账 {r.State.Progress.Pacing.Length} 条逐条一致（时刻不变、不重复）{before == after}、仍在第 {r.State.Progress.Act} 幕";
        }

        private static string Fingerprint(CampaignState s) =>
            string.Join(";", (s?.Progress?.Pacing ?? Array.Empty<ActPaceRecord>()).Select(p => $"{p.Id}@{p.Tick}{(p.Migrated ? "M" : "")}"));

        /// <summary>按分钟逐步推进真实世界（每 10 秒检查一次核心是否丢失）。</summary>
        private static void Wait(BotRun run, CampaignState s, double minutes)
        {
            long end = GameClock.Ticks + GameClock.TicksFor(Math.Max(0.0, minutes) * 60.0);
            while (GameClock.Ticks < end)
            {
                WorldSimulation.StepMany((int)Math.Min(end - GameClock.Ticks, GameClock.StepHz * 10L));
                Maintain(run, s);
                foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
                {
                    if (p != null && p.State >= RaidDirectorService.StateScheduled && run.SeenPlans.Add(p.PlanId))
                    {
                        // 玩家看到新的突袭预警就补防线（第一座炮塔建成之后；BAL-REC-006：严酷难度突袭来得更早更密，按“突袭结束后才补”会在第二次静默夜合并波前只有 3 座）。
                        if (s.BuildingRecords.Any(b => b != null && b.BuildingTypeId == TurretCatalog.LightTypeId) || run.PendingTurrets.Count > 0)
                        {
                            run.TurretsOwed += (int)T("bal.bot.turrets_extra");
                        }
                        else
                        {
                            // 复审修复轮实测：严酷第一次静默夜（第 113 分预警、第 119 分抵达）早于时间线上的第一座炮塔（2:00），6 台 40 秒拆掉核心；
                            // 玩家看到预警而家里一座炮塔都没有时先建一座（轻型炮塔开局就能建）。
                            run.TurretsOwed = Math.Max(run.TurretsOwed, 1);
                        }
                        run.PlanLines.Add($"{p.PlanId} {p.Level} 级 {p.Trigger}{(p.FirstRaid ? " 首袭" : "")} 排定于第 {Min(GameClock.Ticks):F0} 分、抵达第 {Min(p.ArrivalTick):F0} 分（暴露 {s.SignalExposure:F0}）");
                    }
                }
                BuildingRecord coreRec = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
                if (coreRec != null && coreRec.Health < BuildingOps.MaxDurability(coreRec.BuildingTypeId) - 0.5f && run.CoreHitTick < 0)
                {
                    run.CoreHitTick = GameClock.Ticks;
                }
                if (RaidResultService.IsCoreLost(s))
                {
                    run.CoreLost = true;
                    run.PlanLines.Add($"核心第一次掉血在第 {run.CoreHitTick / (double)GameClock.StepHz:F0} 秒、丢失在第 {GameClock.Ticks / (double)GameClock.StepHz:F0} 秒");
                    throw new InvalidOperationException($"核心在第 {Min(GameClock.Ticks):F0} 分被摧毁（暴露 {s.SignalExposure:F0}；突袭计划 [{string.Join("；", run.PlanLines)}]；防线 {DefenseLine(s)}）");
                }
            }
        }

        /// <summary>
        /// 玩家模型的维护动作（FG16 没写、正常玩家都会做的事）：每次突袭结束 bal.bot.repair_minutes 后修好 / 重建家园里受损和被摧毁的建筑
        /// （维修、重建的施工过程由维修无人机站 / 自动重建自检覆盖，机器人只取“修好了”这个结果）；每次收到新的突袭预警（第一座炮塔之后）补建 bal.bot.turrets_extra 座轻型炮塔（至多 bal.bot.turrets_max × 难度的突袭规模倍率座）。
        /// 突袭进行中（攻城剧场开着）不动，等打完再做。
        /// </summary>
        private static void Maintain(BotRun run, CampaignState s)
        {
            // 暴露管理（FG06 / FG16 第 10 节：关闭信号塔广播是玩家压暴露的手段，图鉴与暴露面板都写着；FGJ-M6 同一做法）：高于 off_at 关广播、低于 on_at 再打开。
            if (!run.BroadcastOff && s.SignalExposure >= T("bal.bot.exposure_off_at"))
            {
                CampaignExposureLedger.SetTowerBroadcastOff(s, true);
                run.BroadcastOff = true;
                run.BroadcastToggles++;
            }
            else if (run.BroadcastOff && s.SignalExposure <= T("bal.bot.exposure_on_at"))
            {
                CampaignExposureLedger.SetTowerBroadcastOff(s, false);
                run.BroadcastOff = false;
            }
            int ended = RaidResultService.All(s).Count(r => r != null && r.EndTick >= 0);
            if (ended > run.ResultsSeen)
            {
                run.ResultsSeen = ended;
                run.RepairAt = GameClock.Ticks + GameClock.TicksFor(T("bal.bot.repair_minutes") * 60.0);
            }
            if (SiegeService.StateOf(s)?.TheaterActive ?? false)
            {
                return;
            }
            // 已付款的炮塔到工期后建成（复审 P1：真实门槛、真实废料、真实工期；施工劳力不模拟）。
            for (int i = run.PendingTurrets.Count - 1; i >= 0; i--)
            {
                if (GameClock.Ticks >= run.PendingTurrets[i])
                {
                    run.PendingTurrets.RemoveAt(i);
                    PlaceNearCore(s, TurretCatalog.LightTypeId, playerGates: true, out _);
                }
            }
            if (run.TurretsOwed > 0)
            {
                int have = s.BuildingRecords.Count(b => b != null && b.BuildingTypeId == TurretCatalog.LightTypeId) + run.PendingTurrets.Count;
                int max = TurretsMax(s);
                if (have >= max)
                {
                    run.TurretsOwed = 0;
                }
                (int cost, float secs) = BuildCost(TurretCatalog.LightTypeId);
                for (; run.TurretsOwed > 0 && have < max; run.TurretsOwed--, have++)
                {
                    Spend(run, s, cost, "补炮塔");
                    run.PendingTurrets.Add(GameClock.Ticks + GameClock.TicksFor(secs));
                }
            }
            if (run.RepairAt < 0 || GameClock.Ticks < run.RepairAt)
            {
                return;
            }
            run.RepairAt = -1;
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || HomeValleyController.IsPlannedGhost(b))
                {
                    continue;
                }
                float max = BuildingOps.MaxDurability(b.BuildingTypeId);
                if (b.Health < max - 0.5f || b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    // 复审 P1：修复按真实修复表付废料（按损失比例，被毁的付全价）。
                    (int full, float _) = RepairCost(b.BuildingTypeId);
                    int cost = b.ConstructionState == BuildingConstructionState.Damaged ? full : (int)Math.Ceiling(full * (1f - Mathf.Clamp01(b.Health / Math.Max(1f, max))));
                    Spend(run, s, cost, "修复 " + b.BuildingTypeId);
                    b.Health = max;
                    b.ConstructionState = BuildingConstructionState.Operational;
                    run.Repairs++;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            DefenseService.Sync(s);
            TurretService.Sync(s);
        }

        /// <summary>防线规模上限随难度的突袭规模放大（BAL-REC-006：严酷 ×1.3 的突袭用标准难度的 9 座炮塔守不住合并波；玩家选了严酷就会多建防线）。</summary>
        private static int TurretsMax(CampaignState s) => Math.Max(1, (int)Math.Round(T("bal.bot.turrets_max") * DifficultyService.Scale(s)));

        private static string DefenseLine(CampaignState s)
        {
            BuildingRecord core = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            var turrets = s.BuildingRecords.Where(b => b != null && b.BuildingTypeId == TurretCatalog.LightTypeId).ToList();
            CombatSite site = WorldSimulation.Home?.Combat;
            string kinds = string.Join(",", new[] { ExposureSourceKind.HighPower, ExposureSourceKind.CoreFire, ExposureSourceKind.RawFire, ExposureSourceKind.AlienTech, ExposureSourceKind.NodeDestroyed, ExposureSourceKind.TowerOff }
                .Select(k => $"{k} {CampaignExposureLedger.TotalAdded(s, k):+0.0;-0.0;0}"));
            return $"暴露来源 [{kinds}]、用电需求 {s.PowerDemand:F0}；核心耐久 {core?.Health:F0}/{(core != null ? BuildingOps.MaxDurability(core.BuildingTypeId) : 0):F0}；轻型炮塔 {turrets.Count} 座（{string.Join(",", turrets.Select(b => $"{b.ConstructionState}/{b.PowerState}/{b.Health:F0}"))}）、内核里 {site?.TurretUnitCount ?? -1} 座、攻城单位 {site?.SiegeUnitCount ?? -1} 台";
        }

        private static double Min(long tick) => tick / (double)GameClock.StepHz / 60.0;

        private static double MarchMinutes(CampaignState s, string site, IReadOnlyList<int> team)
        {
            float route = ExpeditionSiteAnchors.RouteLength(s, site);
            float speed = ExpeditionMarchService.MarchSpeed(team);
            return route > 0 && speed > 0 ? route / speed / 60.0 : 0.0;
        }

        /// <summary>撤离：正式返程（BeginReturnFromSite）后真实行军走回家园（到家结算由行军服务做）；超过预计 3 倍仍没到才用 ReturnNowForTests 收尾并记下。</summary>
        private static void ReturnHome(BotRun run, CampaignState s, string site, List<int> team)
        {
            int[] alive = team.Where(id => Rec(id) != null && Rec(id).IsAlive).ToArray();
            ExpeditionMarchService.BeginReturnFromSite(s, site, alive);
            double expect = site == City ? run.MarchCity : run.MarchFo;
            long cap = GameClock.Ticks + GameClock.TicksFor(Math.Max(2.0, expect * 3.0) * 60.0);
            while (ExpeditionMarchService.IsMarching(s) && GameClock.Ticks < cap)
            {
                WorldSimulation.StepMany(GameClock.StepHz * 2);
            }
            if (ExpeditionMarchService.IsMarching(s))
            {
                ExpeditionMarchService.ReturnNowForTests(s);
                run.Error ??= $"{site} 返程超过预计 3 倍（{expect:F1} 分）仍没到家";
            }
            WorldSimulation.StepMany(GameClock.StepHz * 2);
        }

        /// <summary>
        /// 在核心周围找一格放下 <paramref name="type"/>。<paramref name="playerGates"/> = true 时按玩家新放置校验（解锁 / 研发 / 节奏门槛、数量上限、炮塔蓝图）——
        /// 门槛没开就抛出（机器人做了真实玩家做不到的事 = 自检失败，复审 P1）。
        /// </summary>
        private static BuildingRecord PlaceNearCore(CampaignState s, string type, bool playerGates, out GridPlacementResult check)
        {
            GridCell core = HomeGridService.CorePivot(s);
            check = null;
            for (int r = 6; r < 40; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    foreach (int dy in new[] { -r, r })
                    {
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        GridPlacementResult v = HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: playerGates, checkCost: false);
                        if (v.Ok)
                        {
                            check = v;
                            return Register(s, type, c);
                        }
                        if (playerGates && v.Reasons.Count > 0 && v.Reasons[0].Code is GridBlockReason.Locked or GridBlockReason.MaxCount or GridBlockReason.NotPlaceable or GridBlockReason.TurretNoBlueprint)
                        {
                            throw new InvalidOperationException($"{type} 的玩家门槛没开（{v.Reasons[0].Code}）：机器人不能替玩家绕过");
                        }
                    }
                }
            }
            throw new InvalidOperationException($"核心周围放不下 {type}");
        }

        /// <summary>主时间线上的一次建造：真实门槛（playerGates）→ 真实花费（<see cref="Spend"/>）→ 真实工期 → 建成。</summary>
        private static void Build(BotRun run, CampaignState s, string type, bool playerGates)
        {
            (int cost, float secs) = BuildCost(type);
            Spend(run, s, cost, "建造 " + type);
            Wait(run, s, secs / 60.0);
            PlaceNearCore(s, type, playerGates, out _);
        }

        private static (int Cost, float Seconds) BuildCost(string type) =>
            HomeValleyLayout.BuildProfile.TryGetValue(type, out (int ScrapCost, float Seconds) c) ? (c.ScrapCost, c.Seconds) : (0, 0f);

        private static (int Cost, float Seconds) RepairCost(string type) =>
            HomeValleyLayout.RepairProfile.TryGetValue(type, out (int ScrapCost, float Seconds) c) ? (c.ScrapCost, c.Seconds) : BuildCost(type);

        /// <summary>
        /// 机器人的一笔花费（复审 P1）：先从真实废料里走经济账本扣（提议 → 预留 → 提交）；真实废料不够的部分记成“经济缺口”——
        /// 机器人不玩家园经济（不建回收站 / 不拆残骸 / 不领任务奖励 / 不打驻军拿掉落），缺口 = 真实玩家这时必须靠经济操作挣到的废料。
        /// shortcut: 缺口只记录、不判失败（每次花费都写进报告），机器人学会经营后改成缺口 = 0 才算过；承接 DEBT-FG9BAL01-02（FG15-BAL-01）。
        /// </summary>
        private static void Spend(BotRun run, CampaignState s, int scrap, string what)
        {
            if (scrap <= 0)
            {
                return;
            }
            int real = Math.Min(scrap, Math.Max(0, s.Scrap));
            if (real > 0)
            {
                string tx = "fgbal-bot:" + (_seq++).ToString(CultureInfo.InvariantCulture);
                CampaignEconomyLedger.ProposeConsume(s, tx, "fgbal-bot", CampaignEconomyLedger.ResourceScrap, real);
                if (!CampaignEconomyLedger.Reserve(s, tx).Success || !CampaignEconomyLedger.Commit(s, tx).Success)
                {
                    CampaignEconomyLedger.Cancel(s, tx);
                    real = 0;
                }
            }
            run.ScrapSpent += real;
            int gap = scrap - real;
            if (gap > 0)
            {
                run.ScrapGap += gap;
                if (run.GapLines.Count < 12)
                {
                    run.GapLines.Add($"第 {Min(GameClock.Ticks):F0} 分 {what} 缺 {gap}");
                }
            }
        }

        /// <summary>建成一座建筑（与 FgMilestoneM6SelfCheck.Register 同一写法：施工过程由建造 / 施工自检覆盖，机器人只取“建成”这个事实）。</summary>
        private static BuildingRecord Register(CampaignState s, string type, GridCell c)
        {
            BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#bal" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, 0),
                Health = BuildingOps.MaxDurability(type),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            DefenseService.Sync(s);
            TurretService.Sync(s);
            NavService.SyncGridChanges();
            WorldSimulation.StepMany(2);
            return r;
        }

        private static FracturedCityController EnterCity(CampaignState s, int[] team)
        {
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            RegionRecord region = FracturedCityRegion.Find(s);
            region.State = RegionState.Available;
            region.ExpeditionCount = Math.Max(1, region.ExpeditionCount);
            for (int i = 0; i < team.Length; i++)
            {
                MachineRecord rec = Rec(team[i]);
                MachineRegistry.MoveToRegion(rec, City);
                rec.WorldPosition = FracturedCityLayout.EntryEvac.Position + new Vector2((i % 3 - 1) * 2.5f, -1f);
            }
            FracturedCityController city = WorldSimulation.LoadFracturedCity(team, resume: false);
            SetMarch(s, team, ExpeditionMarchService.PhaseAtSite);
            return city;
        }

        private static void SetMarch(CampaignState s, int[] members, int phase)
        {
            CampaignFgStateDomains.EnsureAll(s);
            ExpeditionMarchState march = ExpeditionMarchService.Current(s);
            if (march == null)
            {
                return;
            }
            march.Members = members;
            march.Phase = phase;
        }

        private static int Spawn(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, at, 4000f, 4000f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static CampaignState NewWorld(int seed, string difficulty)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            SlagHeatService.ResetForTests();
            SiteGenLayout.ResetCache();
            SignalContactService.ResetSessionState();
            CampaignState s = CampaignState.CreateNew("fgbal-" + difficulty + "-" + seed, difficulty, seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            Act1PacingService.OnCampaignEntered(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static float T(string id) => GridContent.TryGetTuning(id, out float v) ? v : throw new InvalidOperationException("fg.TbHomeTuning 缺 " + id);

        // ───────────────────────────── 工具 ─────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgAct1BalanceSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
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

        private static void Line(string s) => _report.AppendLine(s);
    }
}
