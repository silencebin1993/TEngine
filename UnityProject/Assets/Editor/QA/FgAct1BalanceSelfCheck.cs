using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
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
using GameLogic.Campaign.Logistics;
using GameLogic.Core;
using GameLogic.EditorTools.JourneyBots;
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
    ///   FG15-BAL-01（DEBT-FG9BAL01-02）：机器人经营家园——拆开局两处残骸（真实拆解工单）、破碎都市里右键打驻军并走过去捡掉落（到家结算进仓库）、
    ///   传送带开放后建回收站压废墟并铺带进仓库、领指引任务奖励；两场首领战用右键攻击真打（寂听主脑：地下中继大厅；铸造主核心：外围 → 核心门 → 供能节点 → 主核心）；
    ///   超控阵列先建实验室研究 signal.override_t1（需要监听阵列核），建造时投入监听阵列核。三个种子的经济缺口 = 0（G 段），首领战与研发路径逐项断言（H / I 段）。
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

        [MenuItem("QA/自检/FG 第一幕平衡与节奏机器人")]
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
                ProducerCatalog.Reload(); // 生产建筑表按引用持有配方：物品表重载后必须一起重载，否则前面的段建过的表会拒绝配方（只单跑时不暴露）
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
            "bal.bot.leader_fight_minutes", "bal.bot.override_minutes", "bal.bot.core_prep_minutes", "bal.bot.core_fight_minutes", "bal.bot.max_deviation", "bal.bot.retreat_hp_fraction", "bal.bot.boss_team_size", "bal.bot.repair_minutes", "bal.bot.turrets_max", "bal.bot.exposure_off_at", "bal.bot.exposure_on_at",
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
                    $"E{Array.IndexOf(Seeds, seed) + 1} 突袭 vs 第一套防线（种子 {seed}，第一座炮塔起每次突袭预警后补 {(int)T("bal.bot.turrets_extra")} 座、至多 {(int)T("bal.bot.turrets_max")} 座（× 难度突袭规模），FG-GAP-146）：第一幕里真实突袭 {run.RaidLines.Count} 次、核心从未丢失（每次突袭后修好受损建筑 {run.Repairs} 座次；真实花废料 {run.ScrapSpent}、经济缺口 {run.ScrapGap} [{string.Join("；", run.GapLines)}]；暴露 ≥ {T("bal.bot.exposure_off_at"):F0} 关广播 {run.BroadcastToggles} 次）；结算 [{string.Join("；", run.RaidLines)}]");
                int n = Array.IndexOf(Seeds, seed) + 1;
                Expect(run.Error == null && run.ScrapGap == 0 && run.WrecksSalvaged == 2 && run.KitWs.Count > 0 && run.KitsMade > 0 && run.GarrisonsDestroyed > 0 && run.LootScrap > 0 && run.RecyclerScrap > 0 && run.Claims.Count > 0,
                    $"G{n} DEBT-FG9BAL01-02 ① 经营家园（种子 {seed}）：经济缺口 {run.ScrapGap}（应 0，真实花废料 {run.ScrapSpent}）；拆开局残骸 {run.WrecksSalvaged}/2（工单 {run.SalvageOrders}）；" +
                    $"破碎都市右键打掉驻军 {run.GarrisonsDestroyed}/{run.GarrisonsTotal} 处、捡回掉落到家结算进仓库 {run.LootScrap} 废料；回收站 {run.RecyclerIds.Count} 座压废墟（其中 {run.Merged} 座侧向汇入）+ 电塔 {run.RecyclerPoles} 座 + 传送带 {run.RecyclerBelts} 格进仓库，拆出 {run.RecyclerScrap} 废料；发电机 +{run.Generators} 座；补员 {run.Refills} 台、打不过撤离 {run.GarrisonRetreats} 次、回家修机器 {run.Heals} 次；重建被摧毁建筑 {run.Rebuilds} 座次、维修件产线 {run.KitWs.Count} 条（提取钻 → 精炼炉 → 零件工坊 ×2，传送带 {run.KitLineBelts} 格）产出 {run.KitsMade} 件、用维修件 {run.KitsUsed} 件、没维修件留着受损 {run.WornLeft} 座次；花费分类 [{string.Join("，", run.SpendBy.Select(kv => kv.Key + " " + kv.Value))}]；" +
                    $"领指引任务奖励 {run.Claims.Count} 次（{string.Join(",", run.Claims)}，废料 +{run.QuestScrap}）{(run.Notes.Count == 0 ? string.Empty : "；备注：" + string.Join("；", run.Notes))}");
                Expect(run.Error == null && run.SilentMinutes > 0 && run.FoundryMinutes > 0 && run.FoundryPhases.Contains(CoreBossState.Phase2.ToString()) && run.FoundryPhases.Contains(CoreBossState.AntiAir.ToString()) && run.Attacks > 0,
                    $"H{n} DEBT-FG9BAL01-02 ④ 首领战真打（种子 {seed}，右键攻击 {run.Attacks} 次）：寂听主脑 {run.SilentMinutes:F1} 游戏分（玩家模型 {T("bal.bot.leader_fight_minutes"):F1} 分 × 耐久倍率）；" +
                    $"铸造主核心 {run.FoundryMinutes:F1} 游戏分（玩家模型 {T("bal.bot.core_fight_minutes"):F1} 分 × 耐久倍率），阶段 {run.FoundryPhases}");
                Expect(run.Error == null && run.OverrideLockedBefore && run.OverrideResearched && run.KeyConsumed,
                    $"I{n} DEBT-FG9BAL01-02 ③ 超控阵列走研发（种子 {seed}）：研究完成前玩家放置被拒（Locked）{run.OverrideLockedBefore}；实验室研究 signal.override_t1 完成 {run.OverrideResearched}；建造时投入监听阵列核 {run.KeyConsumed}");
                if (seed == 42)
                {
                    Expect(run.TurretCount > 0 && run.DefenseDemand > 0f && run.DefenseDemand <= run.TotalDemand,
                        $"D2 FG-GAP-151 电网重算单列防御用电（真实炮塔 {run.TurretCount} 座）：总用电 {run.TotalDemand:0}、其中防御 {run.DefenseDemand:0}，高功率暴露只按生产 {run.TotalDemand - run.DefenseDemand:0} 换算");
                }
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
                $"首领战用时（机器人参数 × 敌人耐久倍率，只记录不判，复审 P2）{harshFightsLonger}（铸造主核心 {M(harsh, "foundry_core"):F0} 对 {M(std, "foundry_core"):F0} 分）；真实花废料 {harsh.ScrapSpent}、经济缺口 {harsh.ScrapGap}；首领战 寂听 {harsh.SilentMinutes:F1} / 铸造 {harsh.FoundryMinutes:F1} 游戏分；超 25% 的点（只记录，严酷难度 FG16 不要求同一节奏）：{string.Join("；", harshOff)}{(harsh.Error == null ? string.Empty : "；出错：" + harsh.Error)}");
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
            public float TotalDemand;
            public float DefenseDemand;
            public int TurretCount;
            public int Act;
            public string Error;
            public double WallSeconds;
            public long Steps;
            public string SaveLine;
            public bool SaveOk;
            public int FightSeconds;
            public int SalvageOrders;
            public int WrecksSalvaged;
            public readonly List<string> Notes = new List<string>();
            public int GarrisonsDestroyed;
            public int GarrisonsTotal;
            public int LootScrap;
            public readonly List<string> RecyclerIds = new List<string>();
            public int RecyclerBelts;
            public int RecyclerPoles;
            public int Generators;
            public int Refills;
            public int GarrisonRetreats;
            public int Heals;
            public int Rebuilds;
            public int KitsUsed;
            public readonly List<string> KitWs = new List<string>();
            public int KitsMade;
            public int KitLineBelts;
            public int CannonSquad;
            public int Merged;
            public int FoGarrisonsDestroyed;
            public readonly List<(List<GridCell> Path, int GoalDir)> RecyclerRoutes = new List<(List<GridCell>, int)>();
            public int WornLeft;
            public readonly List<long> PendingGens = new List<long>();
            public readonly SortedDictionary<string, int> SpendBy = new SortedDictionary<string, int>(StringComparer.Ordinal);
            public long RecyclerScrap;
            public readonly List<string> Claims = new List<string>();
            public int QuestScrap;
            public int Attacks;
            public double SilentMinutes = -1;
            public double FoundryMinutes = -1;
            public string FoundryPhases = string.Empty;
            public bool OverrideLockedBefore;
            public bool OverrideResearched;
            public bool KeyConsumed;

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
        /// 机器人只做 FG16 第 2 节时间线上的那件事与正常玩家的家园经营（拆残骸、打驻军捡掉落、回收站、领任务奖励）；突袭来了靠它建的炮塔守（第一套防线），不手动操作。
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
                // 0:10 第一批经营：开局两处残骸下拆解工单（真实工单状态机：家园机器走过去拆，废料走地面物 → 仓库，ER3-STO-01）。
                StartSalvage(run, s);
                // 0:20 造出第一台战斗履带机：付装配站真实生产价（FactoryProduceDefaults 废料 + 工期）。
                Wait(run, s, T("bal.bot.machine_minutes"));
                HomeValleyLayout.ProduceBlueprintDefault mdef = HomeValleyLayout.FactoryProduceDefaults[HomeValleyLayout.BlueprintErc003Id];
                Spend(run, s, Price(s, HomeValleyLayout.BlueprintErc003Id, mdef.ScrapCost), "生产履带机");
                Wait(run, s, mdef.Seconds / 60.0);
                var team = new List<int> { Spawn(s, new Vector2(4f, 4f)) };
                team.AddRange(s.MachineRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && !team.Contains(m.LogicId)).Select(m => m.LogicId).Take(teamSize - 1));
                // 0:40 第一次远征：真实路线 ÷ 真实行军速度。
                Wait(run, s, T("bal.bot.expedition_prep_minutes"));
                run.WrecksSalvaged = SalvagedCount(s);
                ExpeditionSiteAnchors.EnsureResolved(s);
                run.MarchCity = MarchMinutes(s, City, team);
                run.MarchFo = MarchMinutes(s, Fo, team);
                Wait(run, s, run.MarchCity);
                // 停留期间地点一直载入（队伍在地点 = 地点在运行，ExpeditionMarchService.Reconcile）。
                FracturedCityController city = EnterCity(s, team.ToArray());
                long stayEnd = GameClock.Ticks + GameClock.TicksFor(T("bal.bot.city_stay_minutes") * 60.0);
                // 1:00 第一次接入（在地点里接入一台带接入口的机器——本局事件钩子）。
                Wait(run, s, T("bal.bot.uplink_minutes"));
                GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink);
                // 停留的其余时间：像玩家一样右键打驻军（离入口近的先打），打光一处就走过去捡掉落（自动拾取进货舱）。
                FightGarrisons(run, s, city, team, stayEnd);
                WaitUntil(run, s, stayEnd);
                // 撤离 → 真实返程行军 → 到家结算（货舱卸进仓库）→ 1:30 基础传送带开放。
                int scrapBeforeReturn = s.Scrap;
                city.Exit(true);
                ReturnHome(run, s, City, team);
                HealTeam(run, s, team);
                run.LootScrap = s.Scrap - scrapBeforeReturn;
                // 1:30 传送带开放：回收站压在废墟上、电塔接电、传送带铺进仓库输入口（与 FGJ-M4 同一套规划）；工期算在到第一座炮塔的等待里。
                long turretAt = GameClock.Ticks + GameClock.TicksFor(T("bal.bot.turret_minutes") * 60.0);
                BuildRecycler(run, s);
                // 2:00 第一座炮塔（第一套防线）；第一次突袭由真实突袭导演按暴露 / 宽限期排。
                WaitUntil(run, s, turretAt);
                Build(run, s, TurretCatalog.LightTypeId, playerGates: true);
                // 2:00～4:00 扩产、研究（bal.bot.foundry_prep_minutes 的说明）：第二座回收站（找得到废墟才建）、发电不够先补发电机、建实验室。
                long foundryPrepEnd = GameClock.Ticks + GameClock.TicksFor(T("bal.bot.foundry_prep_minutes") * 60.0);
                // 仓库输入口已经接了第一座的传送带，第二座铺进归还核心的输入口（与 FGJ-M4 的 RecyclerSink 两种去处相同）；先找核心电力覆盖内的，找不到再串电塔去 64 格内找。
                foreach (bool covered in new[] { true, false })
                {
                    try
                    {
                        BuildRecycler(run, s, 64, "核心输入口", coveredOnly: covered);
                        break;
                    }
                    catch (InvalidOperationException e)
                    {
                        run.Notes.Add($"第二座回收站（{(covered ? "覆盖内" : "串电塔")}）没建：" + e.Message);
                    }
                }
                // 维修件产线（受损建筑只能用维修件修，FG04 第 3 节；零件工坊开局就能建）：不建的话核心的伤害一突袭一突袭地累积（第 5 棒实测 1600 → 455 后在 2 级报复突袭里丢核心）。
                BuildKitLine(run, s);
                // 第三、四座回收站：仓库与核心两个输入口都接了回收站（仓库不能新建），带从侧面汇进已有的回收站传送带。
                // 回收站的产出受废墟储量限制（一格拆完变空地），第一幕后半段的重建 / 超控阵列 / 突击队要靠多压几片废墟（第 5 棒 r5i～r5m：三座时种子 20261017 末尾仍缺 105～224）。
                for (int n = 3; n <= 4; n++)
                {
                    try
                    {
                        BuildRecyclerMerge(run, s);
                    }
                    catch (InvalidOperationException e)
                    {
                        run.Notes.Add($"第 {n} 座回收站没建：" + e.Message);
                        break;
                    }
                }
                if (ResearchService.BuiltLabCount(s) == 0)
                {
                    Build(run, s, ResearchService.TypeId, playerGates: true);
                }
                // 4:00 铸造前哨外围。
                WaitUntil(run, s, foundryPrepEnd);
                Wait(run, s, run.MarchFo);
                FoundryOutpostController fo = EnterFoundry(s, team);
                // 到外围停留（FG16 第 2 节 4:00 只要求到达外围；r5k 试过右键打外围驻军，全队打光更糟）。
                long foStayEnd = GameClock.Ticks + GameClock.TicksFor(T("bal.bot.foundry_stay_minutes") * 60.0);
                float foEntryHp = TeamHp(team);
                while (GameClock.Ticks < foStayEnd && TeamHp(team) >= foEntryHp * T("bal.bot.retreat_hp_fraction"))
                {
                    StepSecond(run, s);
                }
                if (GameClock.Ticks < foStayEnd)
                {
                    run.Notes.Add($"铸造前哨外围挨打提前撤离（第 {Min(GameClock.Ticks - t0):F0} 分，队伍 {TeamLine(team)}）");
                }
                fo.Exit(true);
                ReturnHome(run, s, Fo, team);
                HealTeam(run, s, team);
                WaitUntil(run, s, foStayEnd);
                run.Notes.Add($"铸造前哨外围归来：队伍 {TeamLine(team)}");
                // 5:00 寂听主脑（静默领地的地下中继大厅；往返按去破碎都市的真实路线算）：右键攻击真打。
                int bossTeam = Math.Max(teamSize, (int)T("bal.bot.boss_team_size"));
                Refill(run, s, team, bossTeam);
                Wait(run, s, T("bal.bot.silent_prep_minutes") + run.MarchCity);
                run.SilentMinutes = FightSilentMind(run, s, team);
                Wait(run, s, run.MarchCity);
                HealTeam(run, s, team);
                // 6:00 超控阵列 T1：先建实验室研究 signal.override_t1（需要核心保管库里有监听阵列核），研究完成后才能放，建造时投入监听阵列核。
                long overrideAt = GameClock.Ticks + GameClock.TicksFor(T("bal.bot.override_minutes") * 60.0);
                StartOverrideResearch(run, s);
                WaitUntil(run, s, overrideAt);
                WaitResearch(run, s, OverrideResearch);
                IReadOnlyList<BuildMaterialNeed> mats = BuildMaterials.NewBuild(OverrideArray);
                bool matsBefore = BuildMaterials.AllInStock(s, mats);
                Build(run, s, OverrideArray, playerGates: true);
                run.KeyConsumed = mats.Count > 0 && matsBefore && !BuildMaterials.AllInStock(s, mats);
                // 7:30 铸造主核心 → 8:00 第一幕结束（收到“它在等什么”）：右键攻击真打（外围守军 → 核心门 → 供能节点 → 主核心）。
                // 核心门要求重炮已解析：玩家这时手里有重炮，按重炮蓝图生产一支突击队与幸存的老机器一起去（BAL-REC-018 的主核心耐久按 FGJ-M9 的重炮队折算）。
                // 突击队在出征前才生产（第 5 棒 r5c：超控阵列刚建完就下单，种子 20261017 当时废料不够、缺 169；攒在出征前等的这段时间里正好够）。
                FoundryFixtures(s);
                Wait(run, s, T("bal.bot.core_prep_minutes"));
                RaiseCannonSquad(run, s, team, bossTeam);
                Wait(run, s, run.MarchFo);
                run.FoundryMinutes = FightFoundryCore(run, s, team);
                WorldSimulation.StepMany(GameClock.StepHz * 3);
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
            finally
            {
                SignalCoverageService.OverrideForTests = null;
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
            run.TotalDemand = s.PowerDemand;
            run.DefenseDemand = s.DefensePowerDemand;
            run.TurretCount = s.BuildingRecords?.Count(b => b != null && TurretService.IsTurret(b)) ?? 0;
            run.Act = s.Progress?.Act ?? 0;
            run.RecyclerScrap = run.RecyclerIds.Sum(id => ProductionService.TryGet(s, id, out ProductionService.Producer rp) ? rp.Rec.RuinRecovered : 0L);
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
        private static void Wait(BotRun run, CampaignState s, double minutes) =>
            WaitUntil(run, s, GameClock.Ticks + GameClock.TicksFor(Math.Max(0.0, minutes) * 60.0));

        private static void WaitUntil(BotRun run, CampaignState s, long end)
        {
            while (GameClock.Ticks < end)
            {
                WorldSimulation.StepMany((int)Math.Min(end - GameClock.Ticks, GameClock.StepHz * 10L));
                Tick(run, s);
            }
        }

        /// <summary>战斗中逐秒推进（右键命令每秒看一次）；每 10 秒做一次家园那边的维护与核心检查（与 <see cref="Wait"/> 同一节拍）。</summary>
        private static void StepSecond(BotRun run, CampaignState s)
        {
            WorldSimulation.StepMany(GameClock.StepHz);
            if (++run.FightSeconds % 10 == 0)
            {
                Tick(run, s);
            }
        }

        /// <summary>每 10 游戏秒：家园维护、看突袭预警补防线、领已完成的指引任务奖励、核心是否丢失。</summary>
        private static void Tick(BotRun run, CampaignState s)
        {
            Maintain(run, s);
            foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
            {
                if (p != null && p.State >= RaidDirectorService.StateScheduled && run.SeenPlans.Add(p.PlanId))
                {
                    // 玩家看到新的突袭预警就补防线（第一座炮塔建成之后；BAL-REC-006：严酷难度突袭来得更早更密，按“突袭结束后才补”会在第二次静默夜合并波前只有 3 座）。
                    if (s.BuildingRecords.Any(b => b != null && b.BuildingTypeId == TurretCatalog.LightTypeId) || run.PendingTurrets.Count > 0)
                    {
                        run.TurretsOwed += (int)T("bal.bot.turrets_extra") * Math.Max(1, p.Level); // FG15-BAL-01：等级越高补得越多（严酷第一幕静默报复突袭是 2 级）
                    }
                    else
                    {
                        // 复审修复轮实测：严酷第一次静默夜（第 113 分预警、第 119 分抵达）早于时间线上的第一座炮塔（2:00），6 台 40 秒拆掉核心；
                        // 玩家看到预警而家里一座炮塔都没有时先建一座（轻型炮塔开局就能建）。
                        run.TurretsOwed = Math.Max(run.TurretsOwed, 1);
                    }
                    run.PlanLines.Add($"{p.PlanId} {p.Level} 级 {p.Trigger}{(p.FirstRaid ? " 首袭" : "")} 排定于第 {Min(GameClock.Ticks):F0} 分、抵达第 {Min(p.ArrivalTick):F0} 分（暴露 {s.SignalExposure:F0}，核心耐久 {CoreHealth(s):F0}）");
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
            ClaimQuests(run, s);
            CollectKits(run, s);
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
            for (int i = run.PendingGens.Count - 1; i >= 0; i--)
            {
                if (GameClock.Ticks >= run.PendingGens[i])
                {
                    run.PendingGens.RemoveAt(i);
                    PlaceNearCore(s, Generator2, playerGates: true, out _);
                    run.Generators++;
                }
            }
            // 电网缺电（低优先级建筑停工、炮塔降功率）：玩家在电网面板看到就补一座发电机。
            // 被突袭拆掉的发电机等着重建（下面的重建会把它的发电量补回来），玩家不会为它另建一座（第 5 棒：否则拆一座补一座、重建后又多一座）。
            bool genAwaitingRebuild = s.BuildingRecords.Any(b => b != null && (b.BuildingTypeId == Generator2 || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator)
                                                                 && b.ConstructionState == BuildingConstructionState.Damaged);
            if (!genAwaitingRebuild && s.PowerCapacity + 80f * run.PendingGens.Count + 0.5f < s.PowerDemand && run.PendingGens.Count == 0)
            {
                (int gc, float gs) = BuildCost(Generator2);
                Spend(run, s, gc, "补发电机（缺电）");
                run.PendingGens.Add(GameClock.Ticks + GameClock.TicksFor(gs));
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
                // FG15-BAL-01：按游戏的维修规则计价（BuildingOps.TryOrderRepair）——被摧毁的重建付废料（BuildingOps.RebuildScrapFor）；
                // 受损（耐久没满、照常工作）的用维修件（BuildingOps.RepairKitsFor），库里维修件不够就不修（玩家同样派不出维修单）。
                if (b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    int scrap = BuildingOps.RebuildScrapFor(b);
                    if (scrap < 0)
                    {
                        continue;
                    }
                    Spend(run, s, scrap, "重建 " + b.BuildingTypeId);
                    b.Health = max;
                    b.ConstructionState = BuildingConstructionState.Operational;
                    run.Repairs++;
                    run.Rebuilds++;
                }
                else if (BuildingOps.IsWorn(b))
                {
                    int kits = BuildingOps.RepairKitsFor(b);
                    if (kits > 0 && HomeInventory.Stock(s, BuildingOps.RepairKitId) >= kits)
                    {
                        HomeInventory.RemoveUpTo(s, ItemCatalog.Find(BuildingOps.RepairKitId), kits);
                        b.Health = max;
                        run.Repairs++;
                        run.KitsUsed += kits;
                    }
                    else
                    {
                        run.WornLeft++;
                    }
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            DefenseService.Sync(s);
            TurretService.Sync(s);
        }

        private static float CoreHealth(CampaignState s) => s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)?.Health ?? -1f;

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

        /// <summary>主时间线上的一次建造：真实门槛（playerGates）→ 废料之外的材料（fg.TbBuildMaterial，如超控阵列的监听阵列核）在库 → 真实花费（<see cref="Spend"/>）→ 真实工期 → 建成并投入材料。</summary>
        private static void Build(BotRun run, CampaignState s, string type, bool playerGates)
        {
            IReadOnlyList<BuildMaterialNeed> mats = BuildMaterials.NewBuild(type);
            if (playerGates && !BuildMaterials.AllInStock(s, mats))
            {
                throw new InvalidOperationException($"{type} 缺材料：{BuildMaterials.DescribeShortfall(s, mats)}");
            }
            if (type != Generator2 && !TurretCatalog.IsTurretType(type))
            {
                EnsurePower(run, s, type);
            }
            (int cost, float secs) = BuildCost(type);
            Spend(run, s, cost, "建造 " + type);
            Wait(run, s, secs / 60.0);
            PlaceNearCore(s, type, playerGates, out _);
            foreach (BuildMaterialNeed m in mats)
            {
                HomeInventory.RemoveUpTo(s, m.Item, m.Amount); // 施工只取“建成”的结果：完工记为投入（BuildMaterials 类注释）
            }
        }

        private static (int Cost, float Seconds) BuildCost(string type) =>
            HomeValleyLayout.BuildProfile.TryGetValue(type, out (int ScrapCost, float Seconds) c) ? (c.ScrapCost, c.Seconds) : (0, 0f);

        private static (int Cost, float Seconds) RepairCost(string type) =>
            HomeValleyLayout.RepairProfile.TryGetValue(type, out (int ScrapCost, float Seconds) c) ? (c.ScrapCost, c.Seconds) : BuildCost(type);

        /// <summary>
        /// 机器人的一笔花费（复审 P1）：先从真实废料里走经济账本扣（提议 → 预留 → 提交）；真实废料不够的部分记成“经济缺口”。
        /// FG15-BAL-01（DEBT-FG9BAL01-02）：机器人经营家园后三个种子的缺口必须 = 0（G 段断言），缺口明细照常写进报告。
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
            string kind = what.Split(' ')[0];
            run.SpendBy[kind] = (run.SpendBy.TryGetValue(kind, out int had) ? had : 0) + scrap;
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

        // ───────────────────────────── 家园经营（DEBT-FG9BAL01-02 ①）─────────────────────────────

        /// <summary>开局两处残骸各下一张拆解工单（与点选下令同一个入口 TryCreateSalvage，指给家园里的机器；之后由工单状态机推进）。</summary>
        private static void StartSalvage(BotRun run, CampaignState s)
        {
            List<int> home = s.MachineRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).Select(m => m.LogicId).ToList();
            int i = 0;
            foreach (string node in new[] { HomeValleyLayout.Wreckage1NodeId, HomeValleyLayout.Wreckage2NodeId })
            {
                HomeValleyWorkOrders.WorkOrderOpResult r = HomeValleyWorkOrders.TryCreateSalvage(s, node, home.Count > 0 ? home[i++ % home.Count] : 0);
                if (r.Success)
                {
                    run.SalvageOrders++;
                }
                else
                {
                    run.Notes.Add($"拆 {node} 没下成：{r.FailureReason}");
                }
            }
            HomeValleyWorkOrders.MarkAssignmentDirty();
        }

        private static int SalvagedCount(CampaignState s)
        {
            string[] gone = s.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId)?.DestroyedNodeIds ?? Array.Empty<string>();
            return (gone.Contains(HomeValleyLayout.Wreckage1NodeId) ? 1 : 0) + (gone.Contains(HomeValleyLayout.Wreckage2NodeId) ? 1 : 0);
        }

        /// <summary>领取已完成的指引任务奖励（任务日志“领取”同一入口 TryClaim）。O(任务数)，每 10 游戏秒一次。</summary>
        private static void ClaimQuests(BotRun run, CampaignState s)
        {
            foreach (ObjectiveRecord r in s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
            {
                if (!CampaignObjectiveTracker.CanClaim(r))
                {
                    continue;
                }
                int before = s.Scrap;
                if (CampaignObjectiveTracker.TryClaim(s, r.ObjectiveId, out _))
                {
                    run.Claims.Add(r.ObjectiveId);
                    run.QuestScrap += Math.Max(0, s.Scrap - before);
                }
            }
        }

        /// <summary>破碎都市停留期间：选中队伍、右键离队伍最近的驻军单位（目标死了命令结束，再右键下一台），打光一处就右键地面走到掉落处（自动拾取）。</summary>
        private static void FightGarrisons(BotRun run, CampaignState s, FracturedCityController city, List<int> team, long until) =>
            FightGarrisons(run, s, City, city.Combat, city.SquadCommands, city.SiteGen, team, until);

        /// <summary>
        /// 远征地点停留期间像玩家一样右键打领地驻军（离入口近的先打），打光一处就走过去捡掉落（自动拾取进货舱）。
        /// 破碎都市计入 G 段的驻军数；铸造前哨外围（第 5 棒：原来只站在入口等，三种子都被驻军打掉 2 台、回家再补员）另记，队伍打光就停手（不抛错，回家按真实生产补员）。
        /// </summary>
        private static void FightGarrisons(BotRun run, CampaignState s, string site, CombatSite combat, RegionSquadCommandSystem cmds, SiteGenContent gen, List<int> team, long until)
        {
            bool city = site == City;
            Vector2 entry = SiteGenLayout.EntryLocal(site);
            List<SiteGarrisonPoint> points = gen.Garrisons.OrderBy(g => (g.Local - entry).sqrMagnitude).ToList();
            if (city)
            {
                run.GarrisonsTotal = points.Count;
            }
            float entryHp = TeamHp(team);
            foreach (SiteGarrisonPoint g in points)
            {
                OutpostRecord o = WorldOutpostSystem.Find(s, g.OutpostId);
                if (o == null || o.Destroyed)
                {
                    continue;
                }
                while (GameClock.Ticks < until && !o.Destroyed)
                {
                    if (TeamHp(team) < entryHp * T("bal.bot.retreat_hp_fraction"))
                    {
                        // 复审 P1（真实出厂耐久）：玩家看打不过就停手撤到入口，不把机器送光；剩下的停留时间在入口等。
                        run.Notes.Add($"{CombatSites.SiteName(site)}打驻军受损撤到入口（队伍 {TeamLine(team)}）");
                        run.GarrisonRetreats++;
                        cmds.DebugSelectMany(Alive(team));
                        cmds.IssueMoveTo(entry, false);
                        return;
                    }
                    int[] alive = Alive(team);
                    if (alive.Length == 0)
                    {
                        if (city)
                        {
                            throw new InvalidOperationException("破碎都市里远征队全灭");
                        }
                        run.Notes.Add(CombatSites.SiteName(site) + "里远征队全灭");
                        return;
                    }
                    if (!alive.Any(id => cmds.TryGetActiveCommandKind(id, out RegionCommandKind k) && k == RegionCommandKind.Attack))
                    {
                        string target = Nearest(s, combat, site, alive, (e, at) => SiteGarrisonService.OutpostOfInstance(e.EnemyInstanceId) == g.OutpostId);
                        if (target == null)
                        {
                            break;
                        }
                        cmds.DebugSelectMany(alive);
                        cmds.IssueAttack(target, false);
                        run.Attacks++;
                    }
                    StepSecond(run, s);
                }
                if (!o.Destroyed)
                {
                    break;
                }
                if (city)
                {
                    run.GarrisonsDestroyed++;
                }
                else
                {
                    run.FoGarrisonsDestroyed++;
                }
                cmds.DebugSelectMany(Alive(team));
                cmds.IssueMoveTo(g.Local, false);
                for (int i = 0; i < 12 && GameClock.Ticks < until; i++)
                {
                    StepSecond(run, s);
                }
            }
        }

        /// <summary>
        /// 回收站：与 FGJ-M4 同一套规划（核心 30 格内、压着至少 8 格废墟、输出口能铺带到仓库输入口；FgjM4Common.PlanRecycler / PlanPoles）；
        /// 真实门槛（传送带 T1 已开放、回收站 / 电塔按玩家放置校验）→ 真实花费（回收站 + 电塔 + 每格传送带）→ 工期 → 建成，传送带逐格放进物流内核。
        /// </summary>
        private static void BuildRecycler(BotRun run, CampaignState s, int radius = 30, string sinkName = "仓库输入口", bool coveredOnly = false)
        {
            GridCell corePivot = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            Func<GridCell, bool> accept = coveredOnly ? p => FgjM4Common.NearestDist(FgjM4Common.Recycler, p, 0, corePivot) <= coreR - 0.5f : (Func<GridCell, bool>)null;
            if (!BuildCatalog.TryGet(FgjM4Common.BeltT1, out BuildEntry belt) || !BuildCatalog.IsUnlocked(s, belt))
            {
                throw new InvalidOperationException("传送带 T1 还没开放（节奏点 belts_open）：机器人不能替玩家绕过");
            }
            var plan = new FgjM4Common.FactoryPlan();
            FgjM4Common.Route route = FgjM4Common.PlanRecycler(s, plan, sinkName, out FgjM4Common.Placed rec, out string why, radius, accept);
            if (route == null)
            {
                throw new InvalidOperationException("回收站放不下：" + why);
            }
            string pole = BuildCatalog.TryGet(FgjM4Common.PoleT2, out BuildEntry t2) && BuildCatalog.IsUnlocked(s, t2) ? FgjM4Common.PoleT2 : PoleT1; // 第一幕节奏门槛下 T2 电塔还没开放时用 T1
            List<GridCell> poles = FgjM4Common.PlanPoles(s, plan, new[] { rec }, out string powerWhy, pole)
                                   ?? throw new InvalidOperationException($"回收站接不上电：{powerWhy}（核心旁 (0,8) 放电塔：{HomeGridService.ValidatePlacement(s, FgjM4Common.PoleT2, new GridCell(corePivot.X, corePivot.Y + 8), 0, asPlayerPlacement: true, checkCost: false).Reasons.FirstOrDefault().Code}）");
            GridPlacementResult v = HomeGridService.ValidatePlacement(s, FgjM4Common.Recycler, rec.Pivot, 0, asPlayerPlacement: true, checkCost: false);
            if (!v.Ok)
            {
                throw new InvalidOperationException($"回收站的玩家放置被拒（{v.Reasons.FirstOrDefault().Code}）");
            }
            EnsurePower(run, s, FgjM4Common.Recycler);
            (int rc, float rs) = BuildCost(FgjM4Common.Recycler);
            (int pc, float ps) = BuildCost(pole);
            int cells = route.Path.Count;
            Spend(run, s, rc, "建造 回收站");
            Spend(run, s, pc * poles.Count, "建造 电塔 ×" + poles.Count);
            Spend(run, s, belt.Tool.ScrapPerCell * cells, "铺传送带 ×" + cells);
            Wait(run, s, Math.Max(rs, Math.Max(ps, HomeValleyConstruction.BeltSecondsPerCell * cells)) / 60.0);
            foreach (GridCell c in poles)
            {
                if (!HomeGridService.ValidatePlacement(s, pole, c, 0, asPlayerPlacement: true, checkCost: false).Ok)
                {
                    throw new InvalidOperationException($"电塔 {pole} ({c.X},{c.Y}) 的玩家放置被拒");
                }
                Register(s, pole, c);
            }
            BuildingRecord r = Register(s, FgjM4Common.Recycler, rec.Pivot);
            for (int i = 0; i < cells; i++)
            {
                int dir = i + 1 < cells ? FgjM3Common.DirOf(route.Path[i], route.Path[i + 1]) : route.GoalDir;
                BeltOpResult b = BeltNetworkService.TryPlace(s, route.Path[i], (BeltDir)dir, belt.Tool.Tier);
                if (!b.Ok)
                {
                    throw new InvalidOperationException($"传送带第 {i + 1}/{cells} 格 ({route.Path[i].X},{route.Path[i].Y}) 放不下");
                }
            }
            BeltPortService.Sync(s);
            ProductionService.Sync(s, force: true);
            run.RecyclerIds.Add(r.BuildingId);
            run.RecyclerBelts += cells;
            run.RecyclerRoutes.Add((route.Path, route.GoalDir));
            run.RecyclerPoles += poles.Count;
        }

        /// <summary>
        /// 维修件产线（FG04 第 3 节配方：精炼炉 金属矿 2 → 合金 1；零件工坊 合金 1 → 零件 2、零件 2 → 维修件 1；零件工坊开局就能建）：
        /// 用 FGJ-M4 的工厂块选址（PlanBlocks：金属矿脉旁、能放、优先在核心配电范围里），只取块里的精炼炉与两座零件工坊，提取钻接矿进精炼炉、两段传送带串起来；
        /// 维修件留在第二座零件工坊的输出缓存里，由 <see cref="CollectKits"/> 像玩家一样点“清空缓存到仓库”收进仓库（仓库与核心两个输入口已经接了回收站）。
        /// 真实门槛、造价、工期、放置校验与配方入口；施工劳力不模拟（与 <see cref="BuildRecycler"/> 同一口径）。
        /// </summary>
        private static void BuildKitLine(BotRun run, CampaignState s)
        {
            if (!BuildCatalog.TryGet(FgjM4Common.BeltT1, out BuildEntry belt) || !BuildCatalog.IsUnlocked(s, belt))
            {
                throw new InvalidOperationException("传送带 T1 还没开放：维修件产线铺不了带");
            }
            var fails = new List<string>();
            List<GridCell> origins = KitLineOrigins(s, out string blockWhy);
            foreach (GridCell origin in origins)
            {
                var plan = new FgjM4Common.FactoryPlan { Origin = origin };
                var furnace = new FgjM4Common.Placed { Key = "kitFurnace", Type = FgjM4Common.Furnace, Pivot = origin, Recipe = "alloy_ore" };
                var parts = new FgjM4Common.Placed { Key = "kitParts", Type = FgjM4Common.PartsWs, Pivot = FgjM4Common.Add(origin, KitStride, 0), Recipe = "part" };
                var kits = new FgjM4Common.Placed { Key = "kitKits", Type = FgjM4Common.PartsWs, Pivot = FgjM4Common.Add(origin, KitStride * 2, 0), Recipe = BuildingOps.RepairKitId };
                foreach (FgjM4Common.Placed b in new[] { furnace, parts, kits })
                {
                    plan.Buildings.Add(b);
                    FgjM4Common.Reserve(plan, b.Type, b.Pivot, 0);
                }
                FgjM4Common.PortOutside(furnace.Type, furnace.Pivot, 0, furnace.Type + ".in0", out GridCell fIn, out int fInFace);
                if (FgjM4Common.PlanDrill(s, plan, "kitDrill", "ore_metal", fIn, FgjM4Common.Opp(fInFace), 40, out _, out string drillWhy) == null)
                {
                    fails.Add(FgjM4Common.Cell(plan.Origin) + " 提取钻：" + drillWhy);
                    continue;
                }
                bool linked = true;
                foreach ((FgjM4Common.Placed from, FgjM4Common.Placed to) in new[] { (furnace, parts), (parts, kits) })
                {
                    FgjM4Common.PortOutside(from.Type, from.Pivot, 0, from.Type + ".out0", out GridCell o, out int oFace);
                    FgjM4Common.PortOutside(to.Type, to.Pivot, 0, to.Type + ".in0", out GridCell i, out int iFace);
                    HashSet<long> avoid = FgjM4Common.BaseAvoid(s);
                    avoid.UnionWith(plan.Used);
                    List<GridCell> path = FgjM4Common.PlanRoute(s, o, i, FgjM4Common.Opp(iFace), FgjM4Common.Opp(oFace), avoid, 10, FgjM4Common.BeltOk(s));
                    if (path == null)
                    {
                        fails.Add($"{FgjM4Common.Cell(plan.Origin)} {from.Key} → {to.Key} 铺不通");
                        linked = false;
                        break;
                    }
                    plan.Routes.Add(new FgjM4Common.Route { Key = from.Key + ">" + to.Key, Path = path, GoalDir = FgjM4Common.Opp(iFace) });
                    foreach (GridCell c in path)
                    {
                        plan.Used.Add(FgjM4Common.Key(c));
                    }
                }
                if (!linked)
                {
                    continue;
                }
                string pole = BuildCatalog.TryGet(FgjM4Common.PoleT2, out BuildEntry t2) && BuildCatalog.IsUnlocked(s, t2) ? FgjM4Common.PoleT2 : PoleT1;
                List<GridCell> poles = FgjM4Common.PlanPoles(s, plan, plan.Buildings, out string powerWhy, pole);
                if (poles == null)
                {
                    fails.Add(FgjM4Common.Cell(plan.Origin) + " 接不上电：" + powerWhy);
                    continue;
                }
                FgjM4Common.Placed bad = plan.Buildings.FirstOrDefault(b => !HomeGridService.ValidatePlacement(s, b.Type, b.Pivot, 0, asPlayerPlacement: true, checkCost: false).Ok);
                if (bad != null)
                {
                    fails.Add($"{FgjM4Common.Cell(plan.Origin)} {bad.Type} 的玩家放置被拒");
                    continue;
                }
                int cells = plan.Routes.Sum(r => r.Path.Count);
                float secs = HomeValleyConstruction.BeltSecondsPerCell * cells;
                foreach (FgjM4Common.Placed b in plan.Buildings)
                {
                    EnsurePower(run, s, b.Type);
                    (int bc, float bs) = BuildCost(b.Type);
                    Spend(run, s, bc, "建造 维修件产线 " + b.Type);
                    secs = Math.Max(secs, bs);
                }
                (int pc, float ps) = BuildCost(pole);
                Spend(run, s, pc * poles.Count, "建造 电塔 ×" + poles.Count);
                Spend(run, s, belt.Tool.ScrapPerCell * cells, "铺传送带 ×" + cells);
                Wait(run, s, Math.Max(secs, ps) / 60.0);
                foreach (GridCell c in poles)
                {
                    Register(s, pole, c);
                }
                var ids = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (FgjM4Common.Placed b in plan.Buildings)
                {
                    ids[b.Key] = Register(s, b.Type, b.Pivot).BuildingId;
                }
                foreach (FgjM4Common.Route r in plan.Routes)
                {
                    for (int i = 0; i < r.Path.Count; i++)
                    {
                        int dir = i + 1 < r.Path.Count ? FgjM3Common.DirOf(r.Path[i], r.Path[i + 1]) : r.GoalDir;
                        if (!BeltNetworkService.TryPlace(s, r.Path[i], (BeltDir)dir, belt.Tool.Tier).Ok)
                        {
                            throw new InvalidOperationException($"维修件产线 {r.Key} 第 {i + 1}/{r.Path.Count} 格放不下");
                        }
                    }
                }
                BeltPortService.Sync(s);
                ProductionService.Sync(s, force: true);
                foreach (FgjM4Common.Placed b in plan.Buildings.Where(b => b.Recipe != null))
                {
                    if (!ProductionService.TrySetRecipe(s, ids[b.Key], b.Recipe, out string msg))
                    {
                        throw new InvalidOperationException($"{b.Type} 设配方 {b.Recipe} 被拒：{msg}");
                    }
                }
                run.KitWs.Add(ids[kits.Key]);
                run.KitLineBelts += cells;
                run.RecyclerPoles += poles.Count;
                return;
            }
            throw new InvalidOperationException("维修件产线放不下：" + (fails.Count == 0 ? blockWhy : string.Join("；", fails.Take(3))));
        }

        /// <summary>三座 3×3 自西向东排开的间距（中间留 2 格铺带）。</summary>
        private const int KitStride = 5;

        /// <summary>
        /// 维修件产线的候选原点（精炼炉枢轴）：离核心最近的金属矿脉周围 40 格内逐圈找，精炼炉与两座零件工坊（东边 5、10 格）都能放（放置校验，不看造价）、
        /// 不压已有建筑与端口外侧格；三座都在核心配电范围里的排前面，其次离矿近。最多 8 个。O(40² × 3) 次放置校验，一局一次。
        /// </summary>
        private static List<GridCell> KitLineOrigins(CampaignState s, out string why)
        {
            why = null;
            GridCell core = HomeGridService.CorePivot(s);
            if (!FgjM4Common.NearestTerrain(s, core, "ore_metal", 40, out GridCell ore))
            {
                why = "核心 40 格内没有金属矿脉";
                return new List<GridCell>();
            }
            HashSet<long> avoid = FgjM4Common.BaseAvoid(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            var found = new List<(int score, GridCell o)>();
            int tried = 0;
            for (int r = 3; r <= 40 && found.Count(f => f.score < 1000) < 16; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        GridCell o = FgjM4Common.Add(ore, dx, dy);
                        tried++;
                        var pivots = new[] { (FgjM4Common.Furnace, o), (FgjM4Common.PartsWs, FgjM4Common.Add(o, KitStride, 0)), (FgjM4Common.PartsWs, FgjM4Common.Add(o, KitStride * 2, 0)) };
                        // 两段 2 格的直带（精炼炉 → 零件工坊 → 零件工坊）也要空着能铺。
                        if (!pivots.All(pv => FgjM4Common.Placeable(s, pv.Item1, pv.Item2, 0, avoid))
                            || !new[] { 2, 3, KitStride + 2, KitStride + 3 }.All(x => !avoid.Contains(FgjM4Common.Key(FgjM4Common.Add(o, x, 0))) && FgjM4Common.BeltOk(s)(FgjM4Common.Add(o, x, 0))))
                        {
                            continue;
                        }
                        bool powered = pivots.All(pv => FgjM4Common.NearestDist(pv.Item1, pv.Item2, 0, core) <= coreR - 0.5f);
                        found.Add(((powered ? 0 : 1000) + Math.Abs(dx) + Math.Abs(dy), o));
                    }
                }
            }
            if (found.Count == 0)
            {
                why = $"金属矿脉 {FgjM4Common.Cell(ore)} 周围试了 {tried} 个原点都放不下精炼炉 + 两座零件工坊";
            }
            return found.OrderBy(f => f.score).Take(16).Select(f => f.o).ToList();
        }

        /// <summary>
        /// 每 10 游戏秒：仓库里的维修件少于“维修件保底”预设（fg.TbRulePreset preset.keep_repair_kits，20 件）时，维修件工坊的输出缓存里有货就“清空缓存到仓库”
        /// （面板同一入口 BuildingOps.ClearBuffers；输入缓存里的零件一起退回仓库）。够了就不收——输出缓存满了工坊自己停（反压），不把仓库塞满挡住回收站的废料（第 5 棒 r5c 实测产出 312 件只用 37 件）。
        /// </summary>
        private static void CollectKits(BotRun run, CampaignState s)
        {
            int keep = ConfigSystem.Instance.Tables.TbRulePreset.GetOrDefault("preset.keep_repair_kits")?.Threshold ?? throw new InvalidOperationException("fg.TbRulePreset 缺 preset.keep_repair_kits");
            int stock = HomeInventory.Stock(s, BuildingOps.RepairKitId);
            // 保底够了、而且家里没有等着修的受损建筑就不收（第 5 棒 r5i：只按保底 20 件收，种子 20261017 有 38 座次因缺维修件没修、拖成被摧毁再重建）。
            if (stock >= keep && stock >= s.BuildingRecords.Where(BuildingOps.IsWorn).Sum(BuildingOps.RepairKitsFor))
            {
                return;
            }
            foreach (string id in run.KitWs)
            {
                if (ProductionService.TryGet(s, id, out ProductionService.Producer p) && ProductionService.Total(p.Rec.Out) > 0)
                {
                    int before = HomeInventory.Stock(s, BuildingOps.RepairKitId);
                    BuildingOps.ClearBuffers(s, id, out _);
                    run.KitsMade += Math.Max(0, HomeInventory.Stock(s, BuildingOps.RepairKitId) - before);
                }
            }
        }

        /// <summary>
        /// 第三座回收站（FG15-BAL-01 第 5 棒：种子 1 / 20261017 只靠两座回收站，第一幕后半段的重建与突击队废料不够）：仓库只有开局那一座（不能新建），
        /// 仓库与核心两个输入口都接了回收站，玩家把第三座的带从侧面汇进已有的回收站传送带（侧向汇入，FGR-LOG-021）。
        /// 核心 64 格内压着至少 8 格废墟的 4×4 位置，按废墟格多、离已有带近优先；电不够串电塔。真实门槛、造价、工期与放置校验（与 <see cref="BuildRecycler"/> 同一口径）。
        /// </summary>
        private static void BuildRecyclerMerge(BotRun run, CampaignState s)
        {
            if (!BuildCatalog.TryGet(FgjM4Common.BeltT1, out BuildEntry belt) || !BuildCatalog.IsUnlocked(s, belt))
            {
                throw new InvalidOperationException("传送带 T1 还没开放");
            }
            GridCell core = HomeGridService.CorePivot(s);
            byte ruin = FgjM4Common.TerrainCode("ruin");
            HashSet<long> avoid = FgjM4Common.BaseAvoid(s);
            foreach ((List<GridCell> path, _) in run.RecyclerRoutes)
            {
                foreach (GridCell c in path)
                {
                    avoid.Add(FgjM4Common.Key(c));
                }
            }
            // 侧向汇入点：已有回收站带的中段格（不含头尾两格），从左右两侧朝它铺进来的那一格是空的。
            var entries = new List<(GridCell at, int dir)>();
            foreach ((List<GridCell> path, int goalDir) in run.RecyclerRoutes)
            {
                for (int i = 1; i + 2 < path.Count; i++)
                {
                    int d = FgjM3Common.DirOf(path[i], path[i + 1]);
                    if (i > 0 && FgjM3Common.DirOf(path[i - 1], path[i]) != d)
                    {
                        continue; // 只从直带侧面汇入
                    }
                    foreach (int side in new[] { FgjM4Common.Left(d), FgjM4Common.Right(d) })
                    {
                        GridCell e = FgjM4Common.Step(path[i], FgjM4Common.Opp(side));
                        if (!avoid.Contains(FgjM4Common.Key(e)) && FgjM4Common.BeltOk(s)(e))
                        {
                            entries.Add((e, side));
                        }
                    }
                }
            }
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("已有回收站带上找不到能侧向汇入的格");
            }
            var cands = new List<(int score, GridCell p)>();
            for (int dy = -64; dy <= 64; dy++)
            {
                for (int dx = -64; dx <= 64; dx++)
                {
                    var p = FgjM4Common.Add(core, dx, dy);
                    int ruins = FgjM4Common.Footprint(FgjM4Common.Recycler, p, 0).Count(c => FgjM4Common.IsTerrain(s, c, ruin));
                    if (ruins >= 8)
                    {
                        cands.Add((ruins * 100 - (Math.Abs(dx) + Math.Abs(dy)), p));
                    }
                }
            }
            string pole = BuildCatalog.TryGet(FgjM4Common.PoleT2, out BuildEntry t2) && BuildCatalog.IsUnlocked(s, t2) ? FgjM4Common.PoleT2 : PoleT1;
            var why = new SortedDictionary<string, int>(StringComparer.Ordinal);
            void No(string k) => why[k] = (why.TryGetValue(k, out int n) ? n : 0) + 1;
            int routed = 0;
            foreach (var cnd in cands.OrderByDescending(x => x.score))
            {
                if (routed >= 40)
                {
                    break;
                }
                if (!FgjM4Common.Placeable(s, FgjM4Common.Recycler, cnd.p, 0, avoid)
                    || !FgjM4Common.PortOutside(FgjM4Common.Recycler, cnd.p, 0, FgjM4Common.Recycler + ".out0", out GridCell outCell, out int outFace) || avoid.Contains(FgjM4Common.Key(outCell)))
                {
                    No("回收站放不下");
                    continue;
                }
                var avoidPlus = new HashSet<long>(avoid);
                foreach (GridCell c in FgjM4Common.Footprint(FgjM4Common.Recycler, cnd.p, 0))
                {
                    avoidPlus.Add(FgjM4Common.Key(c));
                }
                if (FgjM4Common.PortOutside(FgjM4Common.Recycler, cnd.p, 0, FgjM4Common.Recycler + ".in0", out GridCell inCell, out _))
                {
                    avoidPlus.Add(FgjM4Common.Key(inCell));
                }
                List<GridCell> path = null;
                int goalDir = 0;
                foreach ((GridCell at, int dir) in entries.OrderBy(e => Math.Abs(e.at.X - outCell.X) + Math.Abs(e.at.Y - outCell.Y)).Take(3))
                {
                    routed++;
                    path = FgjM4Common.PlanRoute(s, outCell, at, dir, FgjM4Common.Opp(outFace), avoidPlus, 12, FgjM4Common.BeltOk(s));
                    if (path != null)
                    {
                        goalDir = dir;
                        break;
                    }
                }
                if (path == null)
                {
                    No("铺不通");
                    continue;
                }
                var plan = new FgjM4Common.FactoryPlan();
                var rec = new FgjM4Common.Placed { Key = "recycler3", Type = FgjM4Common.Recycler, Pivot = cnd.p };
                plan.Buildings.Add(rec);
                FgjM4Common.Reserve(plan, rec.Type, rec.Pivot, 0);
                foreach (GridCell c in path)
                {
                    plan.Used.Add(FgjM4Common.Key(c));
                }
                List<GridCell> poles = FgjM4Common.PlanPoles(s, plan, new[] { rec }, out _, pole);
                if (poles == null)
                {
                    No("接不上电");
                    continue;
                }
                if (!HomeGridService.ValidatePlacement(s, FgjM4Common.Recycler, rec.Pivot, 0, asPlayerPlacement: true, checkCost: false).Ok)
                {
                    No("玩家放置被拒");
                    continue;
                }
                EnsurePower(run, s, FgjM4Common.Recycler);
                (int rc, float rs) = BuildCost(FgjM4Common.Recycler);
                (int pc, float ps) = BuildCost(pole);
                Spend(run, s, rc, "建造 回收站");
                Spend(run, s, pc * poles.Count, "建造 电塔 ×" + poles.Count);
                Spend(run, s, belt.Tool.ScrapPerCell * path.Count, "铺传送带 ×" + path.Count);
                Wait(run, s, Math.Max(rs, Math.Max(ps, HomeValleyConstruction.BeltSecondsPerCell * path.Count)) / 60.0);
                foreach (GridCell c in poles)
                {
                    Register(s, pole, c);
                }
                BuildingRecord r = Register(s, FgjM4Common.Recycler, rec.Pivot);
                for (int i = 0; i < path.Count; i++)
                {
                    int dir = i + 1 < path.Count ? FgjM3Common.DirOf(path[i], path[i + 1]) : goalDir;
                    if (!BeltNetworkService.TryPlace(s, path[i], (BeltDir)dir, belt.Tool.Tier).Ok)
                    {
                        throw new InvalidOperationException($"第三座回收站的传送带第 {i + 1}/{path.Count} 格 ({path[i].X},{path[i].Y}) 放不下");
                    }
                }
                BeltPortService.Sync(s);
                ProductionService.Sync(s, force: true);
                run.RecyclerIds.Add(r.BuildingId);
                run.RecyclerBelts += path.Count;
                run.RecyclerPoles += poles.Count;
                run.Merged++;
                return;
            }
            throw new InvalidOperationException($"核心 64 格内找不到能放第三座回收站、并能侧向汇入已有回收站带的位置（候选 {cands.Count} 个、汇入点 {entries.Count} 个：{string.Join("、", why.Select(kv => kv.Key + " " + kv.Value))}）");
        }

        private const string Generator2 = "generator_2";
        private const string PoleT1 = "power_pole";

        /// <summary>要建的生产 / 研发建筑会让电网缺电时，先建发电机（generator_2，开局就能建；真实门槛、花费、工期）。电网缺电时低优先级建筑停工（实验室“缺电”）。</summary>
        private static void EnsurePower(BotRun run, CampaignState s, string type)
        {
            float need = HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof) ? prof.PowerDemand : 0f;
            for (int guard = 0; guard < 4 && need > 0f && s.PowerCapacity < s.PowerDemand + need; guard++)
            {
                Build(run, s, Generator2, playerGates: true);
                run.Generators++;
            }
        }

        // ───────────────────────────── 超控阵列走研发（DEBT-FG9BAL01-02 ③）─────────────────────────────

        private const string OverrideArray = "override_array";
        private const string OverrideResearch = "signal.override_t1";

        /// <summary>研究完成前超控阵列的玩家放置被拒（负向）；没有实验室就先建一座（真实门槛与花费），把 signal.override_t1 排进研究队列（研发面板同一入口 TryEnqueue）。</summary>
        private static void StartOverrideResearch(BotRun run, CampaignState s)
        {
            run.OverrideLockedBefore = !ResearchService.IsCompleted(s, OverrideResearch)
                                       && HomeGridService.ValidatePlacement(s, OverrideArray, HomeGridService.CorePivot(s), 0, asPlayerPlacement: true, checkCost: false).Reasons.Any(r => r.Code == GridBlockReason.Locked);
            if (ResearchService.BuiltLabCount(s) == 0)
            {
                Build(run, s, ResearchService.TypeId, playerGates: true);
            }
            if (!ResearchService.TryEnqueue(s, OverrideResearch, out string why))
            {
                throw new InvalidOperationException($"研究 {OverrideResearch} 排不进队列：{why}");
            }
        }

        private static void WaitResearch(BotRun run, CampaignState s, string nodeId)
        {
            long cap = GameClock.Ticks + GameClock.TicksFor(3600.0);
            while (!ResearchService.IsCompleted(s, nodeId))
            {
                if (GameClock.Ticks >= cap)
                {
                    throw new InvalidOperationException($"研究 {nodeId} 一小时没完成（{ResearchService.StatusText(s)}）");
                }
                Wait(run, s, 1.0);
            }
            run.OverrideResearched = true;
        }

        // ───────────────────────────── 首领战真打（DEBT-FG9BAL01-02 ④）─────────────────────────────

        private const string Hall = "relay_hall";

        /// <summary>出征前把阵亡的队员补上（装配站真实生产价 + 工期，与 0:20 第一台同一入口）。</summary>
        private static void Refill(BotRun run, CampaignState s, List<int> team, int teamSize)
        {
            HomeValleyLayout.ProduceBlueprintDefault mdef = HomeValleyLayout.FactoryProduceDefaults[HomeValleyLayout.BlueprintErc003Id];
            while (Alive(team).Length < teamSize)
            {
                Spend(run, s, Price(s, HomeValleyLayout.BlueprintErc003Id, mdef.ScrapCost), "补员 履带机");
                Wait(run, s, mdef.Seconds / 60.0);
                team.RemoveAll(id => Rec(id) == null || !Rec(id).IsAlive);
                team.Add(Spawn(s, new Vector2(4f, 4f)));
                run.Refills++;
            }
        }

        private static string TeamLine(List<int> team) => string.Join(",", team.Select(id => Rec(id) == null ? "-" : (Rec(id).IsAlive ? Rec(id).Health.ToString("F0") : "阵亡")));

        /// <summary>
        /// 复审 P1（真实出厂耐久）：远征回家后像玩家一样把受伤的机器送维修位修满再出征（维修位是开局建筑，按 rules.repair_bay.heal_fraction_per_second 的真实速度等够时长）；
        /// 与建筑维修同一写法（Maintain）：维修过程由维修位自检覆盖，机器人只取“修好了”这个结果。没有可用的维修位就带伤出征（记一条）。
        /// </summary>
        private static void HealTeam(BotRun run, CampaignState s, List<int> team)
        {
            int[] alive = Alive(team);
            float worst = alive.Length == 0 ? 0f : alive.Max(id => Rec(id).MaxHealth > 0f ? 1f - Rec(id).Health / Rec(id).MaxHealth : 0f);
            if (worst <= 0.001f)
            {
                return;
            }
            bool bay = s.BuildingRecords.Any(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeRepairBay && b.ConstructionState == BuildingConstructionState.Operational);
            if (!bay)
            {
                run.Notes.Add($"没有可用的维修位，机器带伤出征（{TeamLine(team)}）");
                return;
            }
            Wait(run, s, worst / StandingRuleService.HealFractionPerSecond / 60.0);
            foreach (int id in alive)
            {
                MachineRecord m = Rec(id);
                m.Health = m.MaxHealth;
                CombatSites.Get(HomeValleyLayout.RegionId)?.SyncMachineHealth(m);
            }
            run.Heals++;
        }

        private static float TeamHp(List<int> team) => team.Sum(id => Rec(id) != null && Rec(id).IsAlive ? Rec(id).Health : 0f);

        private static int[] Alive(List<int> team) => team.Where(id => Rec(id) != null && Rec(id).IsAlive).ToArray();

        /// <summary>地点里离队伍最近、满足条件的活着的敌人（右键目标）。O(地点敌人数)，每游戏秒一次。</summary>
        private static string Nearest(CampaignState s, CombatSite combat, string regionId, IEnumerable<int> team, Func<RegionEnemyRecord, Vector2, bool> ok)
        {
            Vector2 sum = Vector2.zero;
            int n = 0;
            foreach (int id in team)
            {
                if (combat.TryGetMachinePosition(id, out Vector2 p))
                {
                    sum += p;
                    n++;
                }
            }
            string best = null;
            float bestD = float.MaxValue;
            foreach (RegionEnemyRecord e in s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (e == null || !e.IsAlive || e.RegionId != regionId || !combat.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 at) || !ok(e, at))
                {
                    continue;
                }
                float d = n > 0 ? (at - sum / n).sqrMagnitude : 0f;
                if (d < bestD)
                {
                    bestD = d;
                    best = e.EnemyInstanceId;
                }
            }
            return best;
        }

        /// <summary>
        /// 一场真打：每游戏秒看一次——有目标就选中活着的队员右键它（没人在攻击或目标换了才重下命令），没目标就右键地面走向 <paramref name="move"/>；
        /// 队伍全灭或超过 <paramref name="capMinutes"/> 游戏分钟没打完就失败（行为坏掉时会失败，不是空等）。
        /// </summary>
        private static void Fight(BotRun run, CampaignState s, RegionSquadCommandSystem cmds, List<int> team, Func<string> pick, Func<Vector2?> move, Func<bool> done,
            double capMinutes, string label, Func<string> diag, ISet<string> skip = null)
        {
            long t0 = GameClock.Ticks;
            long cap = t0 + GameClock.TicksFor(capMinutes * 60.0);
            string last = null;
            long nextMove = 0; // 第 5 棒：原来 lastMove = long.MinValue，Ticks - long.MinValue 溢出成负数，右键地面永远不下（铸造前哨队伍“不动”的根因）
            int refusedInRow = 0;
            while (!done())
            {
                int[] alive = Alive(team);
                if (alive.Length == 0)
                {
                    throw new InvalidOperationException($"{label}：远征队打了 {Min(GameClock.Ticks - t0):F1} 游戏分后全灭（{diag()}）");
                }
                if (GameClock.Ticks >= cap)
                {
                    throw new InvalidOperationException($"{label}：{capMinutes:F0} 游戏分钟没打完（{diag()}）");
                }
                string target = pick();
                if (target != null)
                {
                    bool attacking = alive.Any(id => cmds.TryGetActiveCommandKind(id, out RegionCommandKind k) && k == RegionCommandKind.Attack);
                    refusedInRow = !attacking && target == last ? refusedInRow + 1 : 0;
                    if (skip != null && refusedInRow >= 3)
                    {
                        skip.Add(target); // 右键三次命令都没挂上（够不着 / 不可选中）：玩家会换一个目标
                        run.Notes.Add($"{label}：右键 {target} 三次没变成攻击命令，换目标");
                        refusedInRow = 0;
                        last = null;
                        continue;
                    }
                    if (!attacking || target != last)
                    {
                        cmds.DebugSelectMany(alive);
                        cmds.IssueAttack(target, false);
                        run.Attacks++;
                        last = target;
                    }
                }
                else if (move?.Invoke() is Vector2 goal && GameClock.Ticks >= nextMove)
                {
                    cmds.DebugSelectMany(alive);
                    cmds.IssueMoveTo(goal, false);
                    nextMove = GameClock.Ticks + GameClock.TicksFor(5.0);
                    last = null;
                }
                StepSecond(run, s);
            }
        }

        /// <summary>
        /// 寂听主脑（FG09 FGR-FAC-006）：队伍走到地下中继大厅入口（行军时长已按真实路线等过）、从入口进入（正式入口 TryEnter，进门前自动存档）；
        /// 阶段一右键离得最近的中继节点（不知道哪个是真的，逐个拆）；主脑现身后右键主脑；全频静默与主脑不可选中时打守卫；击败后走到出口离开。
        /// 夹具：机器人不建信号中继，大厅一带的信号覆盖用 SignalCoverageService.OverrideForTests（与 FgSilentFactionSelfCheck.EnterHall 同一夹具），打完复位。
        /// </summary>
        private static double FightSilentMind(BotRun run, CampaignState s, List<int> team)
        {
            PlannedAnchor gate = InteriorSiteService.EntranceFor(s, Hall) ?? throw new InvalidOperationException("找不到地下中继大厅的入口");
            Vector2 e = TerritoryAnchorChecks.EntryOf(s, gate);
            int[] alive = Alive(team);
            CombatSite home = WorldSimulation.Home.Combat;
            for (int i = 0; i < alive.Length; i++)
            {
                Vector2 at = e + new Vector2(i - alive.Length / 2f, 1f);
                Rec(alive[i]).WorldPosition = at;
                if (home.TryGetMachineUnit(alive[i], out int u))
                {
                    home.SetUnitPosition(u, at);
                }
            }
            HomeGridService.RevealArea(s, e, 30f);
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            WorldSimulation.StepMany(3);
            if (!InteriorSiteService.TryEnter(s, gate.Id, out string why, alive))
            {
                throw new InvalidOperationException("进入地下中继大厅被拒：" + why);
            }
            InteriorSiteController hall = WorldSimulation.Interior(Hall);
            var skip = new HashSet<string>(StringComparer.Ordinal);
            run.Notes.Add($"进入地下中继大厅：{alive.Length} 台（{TeamLine(team)}），阶段 {SilentMindBoss.PhaseOf(s)}");
            long start = GameClock.Ticks;
            string Pick()
            {
                SilentMindPhase ph = SilentMindBoss.PhaseOf(s);
                if (ph == SilentMindPhase.Hiding)
                {
                    string node = Nearest(s, hall.Combat, Hall, Alive(team), (en, at) => SilentMindBoss.NodeIndexOf(en.EnemyInstanceId) >= 0);
                    if (node != null)
                    {
                        return node;
                    }
                }
                bool coreUp = hall.Combat.TryGetEnemyUnit(SilentMindBoss.CoreInstanceId, out int cu) && hall.Combat.UnitHasFlag(cu, CombatUnitFlags.Targetable)
                              && !hall.Combat.UnitHasFlag(cu, CombatUnitFlags.Invulnerable);
                if (coreUp && (ph == SilentMindPhase.Magnetic || ph == SilentMindPhase.Exposed))
                {
                    return SilentMindBoss.CoreInstanceId;
                }
                return Nearest(s, hall.Combat, Hall, Alive(team), (en, at) => !skip.Contains(en.EnemyInstanceId) && !SilentMindBoss.IsBossPart(en.EnemyInstanceId));
            }
            Fight(run, s, hall.SquadCommands, team, Pick, null, () => SilentMindBoss.IsDefeated(s), 60.0, "寂听主脑",
                () => $"阶段 {SilentMindBoss.PhaseOf(s)}、主脑 {SilentMindBoss.Core(s)?.Health:F0}、队伍 {TeamLine(team)}；{string.Join("；", run.Notes)}", skip);
            double minutes = Min(GameClock.Ticks - start);
            hall.SquadCommands.DebugSelectMany(Alive(team));
            hall.SquadCommands.IssueMoveTo(hall.ExitPoint, false);
            bool left = false;
            for (int i = 0; i < 90 && !left; i++)
            {
                StepSecond(run, s);
                left = InteriorSiteService.TryExit(s, Hall, out _);
            }
            if (!left)
            {
                throw new InvalidOperationException("打完寂听主脑后走到出口仍离不开大厅：" + InteriorSiteService.LastReason);
            }
            SignalCoverageService.OverrideForTests = null;
            return minutes;
        }

        private const string BpCannonUplink = "bp_fgbal_cannon_uplink";
        private const string BpCannon = "bp_fgbal_cannon";

        /// <summary>夹具 ②（与 FGJ-M9 m9_fix、FgFoundryFactionSelfCheck.EnterBoss 同一套，DEBT-FG9E2E01-01）：核心门三灯——重炮已解析、熔穿过载已研究、家园留守一台“重炮 + 接入口”机器。只做一次。</summary>
        private static void FoundryFixtures(CampaignState s)
        {
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).Distinct().ToArray();
            CampaignEventLedger.TryGrant(s, BlueprintEditorService.ReactionChargeEventId(MechanicalReactionCatalog.ReactionMeltOverloadId), "BlueprintReactionCharge", 0f);
            foreach ((string id, int uplink) in new[] { (BpCannonUplink, 2), (BpCannon, 0) })
            {
                if (s.BlueprintRecords.Any(b => b.BlueprintId == id))
                {
                    continue;
                }
                BlueprintCircuitBoard bp = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
                if (uplink > 0)
                {
                    bp.TrySetUplink(uplink);
                }
                s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
                {
                    BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { bp.ToVersion(1, 0f) },
                }).ToArray();
                if (uplink > 0)
                {
                    Spawn(s, new Vector2(3f, 3f), BpCannonUplink);
                }
            }
        }

        /// <summary>打铸造主核心前：按重炮蓝图生产至多 <paramref name="size"/> 台（装配站真实生产价与工期，同底盘；废料够几台造几台、至少 1 台），加进远征队（幸存的老机器照样去）。</summary>
        private static void RaiseCannonSquad(BotRun run, CampaignState s, List<int> team, int size)
        {
            HomeValleyLayout.ProduceBlueprintDefault mdef = HomeValleyLayout.FactoryProduceDefaults[HomeValleyLayout.BlueprintErc003Id];
            team.RemoveAll(id => Rec(id) == null || !Rec(id).IsAlive);
            int price = Price(s, BpCannon, mdef.ScrapCost);
            for (int i = 0; i < size; i++)
            {
                if (i > 0 && s.Scrap < price)
                {
                    // 复审修复轮 2：真实耐久（800）下幸存的老机器还能打，玩家废料只够几台就造几台（至少 1 台，核心门要求重炮已解析）；不够的不赊账。
                    run.Notes.Add($"重炮突击队废料只够 {i} 台（每台 {price}，库存 {s.Scrap}）");
                    break;
                }
                Spend(run, s, price, "生产重炮突击队");
                Wait(run, s, mdef.Seconds / 60.0);
                team.Add(Spawn(s, new Vector2(4f + i, 4f), BpCannon));
                run.CannonSquad++;
            }
        }

        private static FoundryOutpostController EnterFoundry(CampaignState s, List<int> team)
        {
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord fr = FoundryOutpostRegion.Find(s);
            fr.State = RegionState.Available;
            fr.ExpeditionCount = Math.Max(1, fr.ExpeditionCount);
            int[] alive = Alive(team);
            for (int i = 0; i < alive.Length; i++)
            {
                MachineRecord rec = Rec(alive[i]);
                MachineRegistry.MoveToRegion(rec, Fo);
                rec.WorldPosition = FoundryOutpostLayout.EntryEvac.Position + new Vector2((i % 3 - 1) * 2.5f, -1f); // 行军到了 = 站在地点入口（与 EnterCity / FgAct1SelfCheck.EnterFoundryTeam 同一处）
            }
            FoundryOutpostController fo = WorldSimulation.LoadFoundryOutpost(alive, resume: false);
            // 不改行军状态：上一次返程留下的行军记录被改成“在地点”后会把队员钉在入口（第 4 棒实测：右键移动全部无效、三台叠在同一点）。
            return fo;
        }

        /// <summary>
        /// 铸造主核心（FG09 FGT-FAC-003）：清掉核心门前（门线以南、Demo 布局宽度内）的守军 → 右键核心区地面过门（首领战开始）→ 右键两个供能节点 → 右键主核心打到摧毁。
        /// 夹具 ①：信号覆盖（同寂听主脑）。夹具 ②（与 FGJ-M9 m9_fix、FgFoundryFactionSelfCheck.EnterBoss 同一套，DEBT-FG9E2E01-01）：核心门三灯——重炮已解析、熔穿过载已研究、家园留守一台“重炮 + 接入口”机器；
        /// 机器人不走这三步的研发与解析（它们各有自检与 FGJ-M9 覆盖），只取“门能开”这个前提。
        /// </summary>
        private static double FightFoundryCore(BotRun run, CampaignState s, List<int> team)
        {
            // 夹具：机器人不建信号中继，铸造前哨的信号覆盖用 SignalCoverageService.OverrideForTests（与地下中继大厅同一夹具；不在覆盖里命令会被拒），RunBot 的 finally 复位。
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            FoundryFixtures(s);
            FoundryOutpostController fo = EnterFoundry(s, team);
            WorldSimulation.StepMany(3);
            RegionRecord region = FoundryOutpostRegion.Find(s);
            long start = GameClock.Ticks;
            string seen = string.Empty;
            var skip = new HashSet<string>(StringComparer.Ordinal);
            string Pick()
            {
                CoreBossState now = FoundryOutpostCoreBoss.GetState(region);
                if (FoundryOutpostCoreBoss.IsInitialized(region) && !seen.EndsWith(now.ToString(), StringComparison.Ordinal))
                {
                    seen = seen.Length == 0 ? now.ToString() : seen + "→" + now;
                }
                if (!FoundryOutpostCoreBoss.IsInitialized(region))
                {
                    return Nearest(s, fo.Combat, Fo, Alive(team), (en, at) => !skip.Contains(en.EnemyInstanceId) && at.y < FoundryOutpostLayout.CoreGateBlockLineY && Mathf.Abs(at.x) <= FoundryOutpostLayout.CameraBoundsHalfExtentX + 4f);
                }
                if (now == CoreBossState.Shielded)
                {
                    RegionEnemyRecord n1 = FoundryOutpostRegion.FindEnemy(s, FoundryOutpostLayout.CoreNode1Id);
                    return n1 != null && n1.IsAlive ? FoundryOutpostLayout.CoreNode1Id : FoundryOutpostLayout.CoreNode2Id;
                }
                return FoundryOutpostLayout.MainCoreId;
            }
            string lastPick = null;
            string Diag()
            {
                RegionEnemyRecord t = lastPick != null ? FoundryOutpostRegion.FindEnemy(s, lastPick) : null;
                int outer = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Count(en => en != null && en.IsAlive && en.RegionId == Fo && fo.Combat.TryGetEnemyPosition(en.EnemyInstanceId, out Vector2 at)
                                                                                         && at.y < FoundryOutpostLayout.CoreGateBlockLineY && Mathf.Abs(at.x) <= FoundryOutpostLayout.CameraBoundsHalfExtentX + 4f);
                string pos = string.Join(",", Alive(team).Select(id => fo.Combat.TryGetMachinePosition(id, out Vector2 mp) ? $"({mp.x:F0},{mp.y:F0})" : "-"));
                string kinds = string.Join(",", Alive(team).Select(id => (fo.SquadCommands.TryGetActiveCommandKind(id, out RegionCommandKind k) ? k.ToString() : "无")
                                                                       + (SignalLinkService.IsInSafeMode(s, id) ? "/安全模式" : "") + (SignalCoverageService.CanReceiveCommand(id) ? "" : "/收不到命令")
                                                                       + "/" + (Rec(id)?.RegionId ?? "-")));
                kinds += "；最近命令事件 " + string.Join(" | ", fo.SquadCommands.RecentEvents.Skip(Math.Max(0, fo.SquadCommands.RecentEvents.Count - 2)));
                return $"目标 {lastPick} 耐久 {t?.Health:F0}/{t?.MaxHealth:F0}、外围剩 {outer}、队伍位置 {pos}、命令 {kinds}、核心门 {FoundryOutpostLayout.CoreZoneEntry.Position}";
            }
            string PickLogged() => lastPick = Pick();
            Fight(run, s, fo.SquadCommands, team, PickLogged, () => FoundryOutpostCoreBoss.IsInitialized(region) ? (Vector2?)null : FoundryOutpostLayout.CoreZoneEntry.Position + new Vector2(0f, 1.5f),
                () => FoundryOutpostCoreBoss.IsInitialized(region) && FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Destroyed, 90.0, "铸造主核心",
                () => $"{Diag()}；阶段 {seen}、门 {region.CoreGateUnlocked}、主核心 {FoundryOutpostRegion.FindEnemy(s, FoundryOutpostLayout.MainCoreId)?.Health:F0}、队伍 {TeamLine(team)}；{string.Join("；", run.Notes)}", skip);
            // 复审修复轮 2：阶段按每游戏秒采样，主核心 600 时对空形态（核心暴露 ×1.5）常在同一秒内被打穿、采样看不到；
            // 进入对空形态时游戏写下 CoreAntiAirEndAtPlaySeconds（FoundryOutpostCoreBoss.TriggerAntiAir，阶段二到门槛必经），按它补记，不放宽“必须经过对空形态”。
            if (!seen.Contains(CoreBossState.AntiAir.ToString()) && region.CoreAntiAirEndAtPlaySeconds > 0f)
            {
                seen += "→" + CoreBossState.AntiAir + "（同一秒内打穿）";
            }
            run.FoundryPhases = seen + "→" + CoreBossState.Destroyed;
            return Min(GameClock.Ticks - start);
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

        private static int Spawn(CampaignState s, Vector2 at, string blueprintId = HomeValleyLayout.BlueprintErc003Id)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, blueprintId, HomeValleyLayout.RegionId, at,
                HomeValleyFactory.ShippedHealth, HomeValleyFactory.ShippedHealth); // 复审 P1：与装配站出厂同一耐久（原 4000 夹具已删）
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        /// <summary>复审 P1：装配站入队时锁定的真实价 = 蓝图当前版本的 ScrapCost（HomeValleyFactory.TryEnqueueProduce 同一来源）；没有版本记录才用生产默认价。</summary>
        private static int Price(CampaignState s, string blueprintId, int fallback)
        {
            BlueprintRecord bp = s.BlueprintRecords?.FirstOrDefault(b => b != null && b.BlueprintId == blueprintId);
            BlueprintVersionRecord v = bp?.Versions?.FirstOrDefault(x => x != null && x.Version == bp.ActiveVersion);
            return v != null ? Math.Max(0, v.ScrapCost) : fallback;
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
