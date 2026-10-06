using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
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
    /// FG6-DEF-09 难度与突袭强度的自动验收（FG06 FGR-DEF-060；FG16 第 3 节；FG09 FGR-FAC-003；FG15 FGR-SYS-081“严酷通关按难度修改记录判定”；FGT-DEF-011）。
    /// 全部起真实系统：真实家园（世界模拟、寻路、侦察巢据点）、真实暴露账本、真实突袭导演与攻城展开（战斗内核单位）、真 UXML 难度面板 / 新游戏面板（按钮真点击）、真文件存读档。
    /// A 数据（难度表四行与源数据、FG16 初值、调参、文本中英、通知类型、钩子、图鉴）；
    /// B 难度说明公开写出对敌人的全部加成（中 / 英；三个预设 + 自定义）；
    /// C FGT-DEF-011 负向：建造者难度下暴露 30 / 60 / 90、4 级周期、骚扰、静默夜 / 巨构 / 净化塔都不发动突袭，只有剧情突袭（规模 ×0.5、预警 ×1.5）；
    /// D 第一次普通突袭在任何难度下都固定 1 级（标准 / 严酷 / 自定义 ×2、三个种子，B25）；第一波被建造者取消后，下一次（哪怕是 4 级周期触发）仍是 1 级；
    /// E 游戏中途修改（真 UXML 难度面板）：二次确认、记录、通知、钩子；与当前相同不弹框；取消不改；改成建造者取消还没预警的普通突袭；已预警的不变；自定义滑条按步长取整；
    ///   4 级周期 / 骚扰计时按频率比例缩放；修改记录截断与“全程保持”判定；
    /// F 真文件存读档（自定义倍率、修改记录、开局难度）、存档卡难度名、旧档（没有难度域）与坏值钳回；
    /// G 暂停中修改、0.5x～3x、观察 / 不观察逐字段一致；H 攻城展开的敌人耐久 / 伤害按难度倍率（战斗内核读数）；
    /// I 新游戏面板（真 UXML）：默认标准、选严酷 / 自定义后“开始”交回所选难度、开局写进存档；J 布局探针与英文；K 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgDifficultySelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static class FgDifficultySelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string PanelUxml = UiKitFolder + "DifficultyPanel.uxml";
        private const string NewGameUxml = UiKitFolder + "NewGamePanel.uxml";
        private const int Slot = 5;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 难度与突袭强度")]
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
            Line("\n[难度与突袭强度] 预设与自定义滑条、公开的难度说明、建造者只有剧情突袭、第一次突袭固定 1 级、中途修改与修改记录、存读档（FG6-DEF-09）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgdiff-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                BuildingOps.Reload();
                BuildMaterials.Reload();
                ResearchCatalog.Reload();
                IntelCatalog.Reload();
                RaidCatalog.Reload();
                SiegeCatalog.Reload();
                TurretCatalog.Reload();
                DefenseCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                IntelService.ResetSessionState();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程）；" +
                     "难度读取 / 修改在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），攻城单位在 AOT 战斗内核；真机另测（FG15-SYS-02）");
                Step(CheckData);
                Step(CheckDescription);
                Step(CheckBuilderStoryOnly);
                Step(CheckFirstRaidLevel1);
                Step(CheckFirstRaidAfterCancel);
                Step(CheckMidGamePanel);
                Step(CheckWarnedUntouchedAndTimers);
                Step(CheckHistoryAndKept);
                Step(CheckScheduledRescale);
                Step(CheckMergedRescaleAndBuilder);
                Step(CheckExemptNonStoryCancelled);
                Step(CheckAnchorsAndPostgame);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckEnemyStats);
                Step(CheckNewGamePanel);
                Step(CheckLayoutAndEnglish);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"难度自检抛异常：{e}");
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
                DifficultyPanelUIToolkit.Close();
                DifficultyPanelUIToolkit.InWorldOverrideForTests = false;
                NewGamePanelUIToolkit.Close();
                IntelService.ResetSessionState();
                RaidDirectorService.ResetSessionState();
                SiegeService.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ItemDistribution.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                BuildMaterials.ResetForTests();
                HomeInventory.ResetSessionState();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
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
            Line($"  · [难度与突袭强度] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static DifficultyChoice P(string id) => DifficultyService.Preset(id);

        /// <summary>与主菜单“新建”同一入口：按种子生成 v2 世界、写开局难度（<see cref="DifficultyService.ApplyNewGame"/>），再载入家园。不写死坐标（B25）。</summary>
        private static CampaignState NewWorld(int seed, DifficultyChoice? difficulty = null, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            DifficultyChoice c = DifficultyService.Normalize(difficulty ?? P(DifficultyService.Standard));
            CampaignState s = CampaignState.CreateNew("fgdiff-" + seed, c.Id, seed);
            DifficultyService.ApplyNewGame(s, c);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 600;
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        private static RaidDirectorState D(CampaignState s) => RaidDirectorService.StateOf(s);
        private static RaidPlanRecord[] Plans(CampaignState s) => D(s)?.Plans ?? Array.Empty<RaidPlanRecord>();
        private static RaidPlanRecord LatestPlan(CampaignState s) => Plans(s).OrderByDescending(p => p.Serial).FirstOrDefault();
        private static long Day(double d) => RaidDirectorService.DayTicks(d);

        private static string FirmwareOf(string faction) =>
            ConfigSystem.Instance.Tables.TbFirmwareKind.DataList.First(r => r != null && r.Faction == faction).Id;

        /// <summary>按真实账本把暴露抬到 ≥ <paramref name="target"/>（贡献记在 <paramref name="faction"/>），再走两步让导演处理触发。</summary>
        private static void Raise(CampaignState s, string faction, float target)
        {
            string fw = FirmwareOf(faction);
            for (int guard = 0; guard < 200 && s.SignalExposure < target; guard++)
            {
                CampaignExposureLedger.GrantRawFire(s, fw);
            }
            WorldSimulation.StepMany(2);
        }

        /// <summary>“读一个第 6 天的存档”：开局宽限已过（只在还没有任何计划时用）。</summary>
        private static void PassGrace(CampaignState s)
        {
            long target = RaidDirectorService.GraceEndTick + Day(0.2);
            if (Plans(s).Length == 0 && GameClock.Ticks < target)
            {
                GameClock.SkipForTests(s, target - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
        }

        private static bool RunUntil(Func<bool> done, int maxGameSeconds)
        {
            long limit = GameClock.Ticks + (long)maxGameSeconds * GameClock.StepHz;
            while (GameClock.Ticks < limit)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(15);
            }
            return done();
        }

        /// <summary>跳过空档到 <paramref name="target"/>：没有行进中的队伍、没有排队的触发、所有计划的下一次事件都在它之后（等于读一个更晚的存档）。</summary>
        private static bool SkipIdle(CampaignState s, long target)
        {
            if (target <= GameClock.Ticks)
            {
                return true;
            }
            if (D(s).Pending.Length > 0 || WorldTransitSystem.Groups(s).Any(g => g != null))
            {
                return false;
            }
            foreach (RaidPlanRecord p in Plans(s))
            {
                if (p.State != RaidDirectorService.StateScheduled || p.WarnTick <= target)
                {
                    return false;
                }
            }
            GameClock.SkipForTests(s, target - GameClock.Ticks);
            return true;
        }

        private static int ExpectedBudget(CampaignState s, RaidPlanRecord p, double scale) =>
            Math.Max(1, (int)Math.Round(RaidCatalog.LevelBudget(p.Level) * RaidCatalog.ActCoef(Math.Max(1, s.Progress?.Act ?? 1)) * scale
                                        * RaidDirectorService.IntervalCoef(D(s), p.CreatedTick) * (p.FirstRaid ? RaidCatalog.FirstRaidScale : 1.0)));

        private static int NotifyCount(string type) => NotificationCenter.History.Where(e => e.Type?.Id == type).Sum(e => Math.Max(1, e.Members.Count));

        private static string LastNotify(string type) =>
            NotificationCenter.History.Where(e => e.Type?.Id == type).OrderByDescending(e => e.Id).FirstOrDefault()?.Latest?.DetailText ?? string.Empty;

        private static bool HasCjk(string text) => text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        private static string Short(string s) => s == null ? string.Empty : s.Length > 160 ? s.Substring(0, 160) + "…" : s;

        /// <summary>真点击：走 UI Toolkit 按钮自己的 Clickable（与鼠标点击同一回调）；按钮禁用时返回 false。</summary>
        private static bool ClickButton(Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            if (invoke == null)
            {
                return false;
            }
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            (int code, string output) = F.RunPython(F.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            string[] dump = output.Replace("\r", string.Empty).Split('\n');
            string cxSrc = dump.FirstOrDefault(l => l.StartsWith("CX\tcodex.raid.difficulty\t", StringComparison.Ordinal)) ?? string.Empty;
            GameConfig.fg.CodexEntry cx = t.TbCodexEntry.GetOrDefault("codex.raid.difficulty");
            string cxRt = cx == null ? "(无)" : string.Join("\t", "CX", cx.Id, cx.Tab, cx.TitleKey, cx.BodyKey, cx.HintKey, cx.Links, cx.Hooks, cx.SortOrder);
            int rdd = dump.Count(l => l.StartsWith("RDD\t", StringComparison.Ordinal));
            Expect(code == 0 && cxSrc == cxRt && rdd == 4 && t.TbRaidDifficulty.DataList.Count == 4 && RaidCatalog.Problems.Count == 0,
                $"A1 难度表四行（数据源 {rdd} 行 = 运行时 {t.TbRaidDifficulty.DataList.Count} 行，逐字段比对在 FgRaidDirectorSelfCheck A1）、图鉴“难度与突袭强度”逐字段 = 数据源；运行时目录无问题" +
                (cxSrc == cxRt ? string.Empty : $"\n源：{cxSrc}\n表：{cxRt}") + (RaidCatalog.Problems.Count > 0 ? "\n" + string.Join("；", RaidCatalog.Problems) : string.Empty));

            GameConfig.fg.RaidDifficulty b = RaidCatalog.Difficulty("Builder");
            GameConfig.fg.RaidDifficulty st = RaidCatalog.Difficulty("Standard");
            GameConfig.fg.RaidDifficulty h = RaidCatalog.Difficulty("Harsh");
            GameConfig.fg.RaidDifficulty cu = RaidCatalog.Difficulty("Custom");
            bool fg16 = b.StoryOnly == 1 && Eq(b.Scale, 0.5f) && Eq(b.Frequency, 1f) && Eq(b.Warning, 1.5f) && Eq(b.EnemyHp, 0.8f) && Eq(b.EnemyDamage, 0.8f) && b.SilentNightDays == 7
                        && st.StoryOnly == 0 && Eq(st.Scale, 1f) && Eq(st.Frequency, 1f) && Eq(st.Warning, 1f) && Eq(st.EnemyHp, 1f) && Eq(st.EnemyDamage, 1f) && st.SilentNightDays == 5
                        && h.StoryOnly == 0 && Eq(h.Scale, 1.3f) && Eq(h.Frequency, 1.3f) && Eq(h.Warning, 0.8f) && Eq(h.EnemyHp, 1.2f) && Eq(h.EnemyDamage, 1.2f) && h.SilentNightDays == 4
                        && b.EventRate < st.EventRate && st.EventRate < h.EventRate
                        && cu.Custom == 1 && cu.StoryOnly == 0 && b.Custom == 0 && st.Custom == 0 && h.Custom == 0
                        && string.Join(",", RaidCatalog.Difficulties.Select(r => r.Id)) == "Builder,Standard,Harsh,Custom"
                        && Eq(DifficultyService.CustomMin, 0.5f) && Eq(DifficultyService.CustomMax, 2f) && Eq(DifficultyService.CustomStep, 0.1f) && DifficultyService.HistoryMax == 32;
            Expect(fg16, "A2 FG16 第 3 节初值：建造者只有剧情 规模 ×0.5 预警 ×1.5 敌人 ×0.8 静默夜每 7 日；标准 ×1 每 5 日；严酷 频率 / 规模 ×1.3 预警 ×0.8 敌人 ×1.2 每 4 日；事件频率 少 < 标准 < 多；" +
                         "“自定义”一行用滑条（范围 ×0.5～×2、步长 0.1）；界面顺序 建造者 / 标准 / 严酷 / 自定义；修改记录最多 32 条");

            (int kc, string kout) = F.RunPython(Path.Combine(F.LocateRepo(), "tools", "cell_tables"),
                "-c \"import fgdata_difficulty as d; print(chr(10).join(r[0] for r in d.DIFFICULTY_TEXTS))\"");
            List<string> keys = kout.Replace("\r", string.Empty).Split('\n').Where(k => k.Length > 0).ToList();
            keys.AddRange(RaidCatalog.Difficulties.SelectMany(r => new[] { r.NameKey, r.DescKey }));
            var bad = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                                      || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en)).ToList();
            bool notify = NotificationCatalog.TryGetType("difficulty_changed", out NotifyTypeDef nt) && nt.Tier == NotifyLevel.Info;
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.DifficultyFirstOpen) && GuidanceHooks.Known.Contains(GuidanceHooks.DifficultyFirstChanged)
                         && cx != null && cx.Hooks.Contains(GuidanceHooks.DifficultyFirstOpen) && cx.Hooks.Contains(GuidanceHooks.DifficultyFirstChanged);
            bool slotName = CampaignSlotText.DifficultyName("Custom") == GameText.Get("difficulty.custom.name") && CampaignSlotText.DifficultyName("Harsh") == GameText.Get("difficulty.harsh.name");
            Expect(kc == 0 && keys.Count > 40 && bad.Count == 0 && notify && hooks && slotName,
                $"A3 文本键中英齐全（{keys.Count} 个，从数据源取键）、通知类型 difficulty_changed（信息）、两个引导钩子登记并挂在图鉴条目上、存档卡显示“自定义 / 严酷”" +
                (bad.Count > 0 ? "；缺：" + string.Join(",", bad.Take(6)) : string.Empty));
        }

        private static bool Eq(float a, float b) => Math.Abs(a - b) < 1e-4f;

        // ── B 公开的难度说明 ───────────────────────────────────────────────────────

        private static void CheckDescription()
        {
            string Desc(DifficultyChoice c) => DifficultyService.Describe(c);
            string b = Desc(P("Builder"));
            string st = Desc(P("Standard"));
            string h = Desc(P("Harsh"));
            string cu = Desc(new DifficultyChoice("Custom", 1.7f, 0.5f, 2f));
            string first = GameText.Get("difficulty.line.first_raid");
            string core = GameText.Get("difficulty.line.core_loss");
            bool builder = b.Contains(GameText.Get("difficulty.line.story_only")) && !b.Contains(GameText.Format("difficulty.line.frequency", "1"))
                           && b.Contains(GameText.Format("difficulty.line.scale", "0.5")) && b.Contains(GameText.Format("difficulty.line.enemy", "0.8", "0.8"))
                           && b.Contains(GameText.Format("difficulty.line.silent_night", 7)) && b.Contains(GameText.Get("difficulty.builder.desc"));
            bool harsh = h.Contains(GameText.Format("difficulty.line.frequency", "1.3")) && h.Contains(GameText.Format("difficulty.line.scale", "1.3"))
                         && h.Contains("×0.8") && h.Contains(GameText.Format("difficulty.line.enemy", "1.2", "1.2")) && h.Contains(GameText.Format("difficulty.line.silent_night", 4));
            bool custom = cu.Contains(GameText.Format("difficulty.line.frequency", "1.7")) && cu.Contains(GameText.Format("difficulty.line.scale", "0.5"))
                          && cu.Contains(GameText.Format("difficulty.line.enemy", "1", "1")) && cu.Contains(GameText.Get("difficulty.custom.desc"));
            bool common = new[] { b, st, h, cu }.All(x => x.Contains(first) && x.Contains(core) && x.Contains(GameText.Get("difficulty.line.record")) && !GameText.ContainsMarker(x));
            // 预警行写出实际的最短预警（游戏时间）：严酷 1.5 × 0.8 = 1.2 小时。
            bool warnText = h.Contains(GameClock.FormatGameDuration(72 * GameClock.DaySeconds / 1440.0)) && Math.Abs(RaidCatalog.MinWarningHours - 1.5f) < 1e-4f; // 1.5 × 0.8 = 72 游戏分钟
            GameSettings.SetLanguage(GameLanguage.En);
            string en = Desc(P("Harsh")) + Desc(P("Builder")) + Desc(new DifficultyChoice("Custom", 1.2f, 1.2f, 1.2f));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(builder && harsh && custom && common && warnText && !HasCjk(en) && en.Contains("×1.2"),
                "B FGR-FAC-003 难度说明逐行公开写出对敌人的全部加成：建造者“只有剧情突袭”、规模 ×0.5、敌人耐久 / 伤害 ×0.8、静默夜每 7 日；严酷 频率 / 规模 ×1.3、预警 ×0.8（写出实际最短预警）、敌人 ×1.2；" +
                "自定义写出滑条倍率（敌人按标准 ×1）；任何难度都写“第一次突袭固定 1 级”“核心被毁仍判失败”“修改记入存档”；英文完整无中文\n  严酷：" + h.Replace("\n", " / "));
        }

        // ── C FGT-DEF-011：建造者只有剧情突袭 ───────────────────────────────────────

        private static void CheckBuilderStoryOnly()
        {
            CampaignState s = NewWorld(6301, P("Builder"));
            PassGrace(s);
            int skipped0 = D(s).TriggersSkipped;
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            Raise(s, CampaignExposureLedger.FactionFoundry, 61f);
            Raise(s, CampaignExposureLedger.FactionFoundry, 95f);
            RaidDirectorService.OnSilentNightStarted(s);
            RaidDirectorService.OnMegastructureStageCompleted(s);
            RaidDirectorService.OnPurificationTowerTargeted(s);
            WorldSimulation.StepMany(2);
            bool level4Armed = D(s).Level4NextTick > GameClock.Ticks;
            // 4 级周期：跳到周期点前（中间没有任何计划，等于读一个更晚的存档），真实走过周期点。
            long l4 = D(s).Level4NextTick;
            bool skipped = level4Armed && Plans(s).Length == 0 && SkipIdle(s, l4 - 2);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            int skippedAll = D(s).TriggersSkipped - skipped0;
            bool none = Plans(s).Length == 0 && D(s).RaidCount == 0 && D(s).History.Length == 0 && skippedAll >= 7;
            // 剧情突袭照常：规模 ×0.5、最短预警 ×1.5。
            RaidDirectorService.RequestStoryRaid(s, "foundry", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = LatestPlan(s);
            bool story = p != null && p.Exempt && p.Level == 2 && p.Budget == ExpectedBudget(s, p, 0.5)
                         && RaidDirectorService.MinWarningTicks(s) == RaidDirectorService.HourTicks(1.5 * 1.5)
                         && RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 200) && p.ArrivalTick - p.WarnTick >= RaidDirectorService.HourTicks(2.25);
            // 之后再跑 1 个游戏日（暴露仍在 90 以上）：只有这一波剧情突袭。
            RunUntil(() => false, (int)(GameClock.DaySeconds / 4));
            bool onlyStory = Plans(s).All(x => x.Exempt) && D(s).History.All(x => x.Trigger == RaidCatalog.TriggerStory);
            Expect(level4Armed && skipped && none && story && onlyStory,
                $"C FGT-DEF-011 建造者难度：暴露越过 30 / 60 / 90、超过 90 的 4 级周期、静默夜 / 巨构阶段 / 净化塔触发都不发动突袭（跳过 {skippedAll} 次、计划 0、骚扰未启用）；" +
                $"剧情突袭照常（2 级，预算 {p?.Budget} = 250 × 0.5 × 间隔系数，最短预警 ×1.5）；之后再跑一段只有这一波剧情突袭");
        }

        // ── D 第一次普通突袭固定 1 级 ──────────────────────────────────────────────

        private static void CheckFirstRaidLevel1()
        {
            var rows = new List<string>();
            bool all = true;
            var cases = new (int Seed, DifficultyChoice C)[]
            {
                (6311, P("Standard")),
                (6312, P("Harsh")),
                (6313, P("Harsh")),
                (6314, new DifficultyChoice("Custom", 2f, 2f, 0.5f)),
            };
            foreach ((int seed, DifficultyChoice c) in cases)
            {
                CampaignState s = NewWorld(seed, c);
                PassGrace(s);
                // 一笔越过 30 / 60 / 90：触发是 3 级，但第一次普通突袭固定 1 级、预算 × raid.first_raid_scale。
                Raise(s, CampaignExposureLedger.FactionFoundry, 95f);
                RaidPlanRecord p = Plans(s).FirstOrDefault(x => !x.Exempt);
                double scale = DifficultyService.Scale(s);
                bool ok = p != null && p.FirstRaid && p.Level == 1 && p.Budget == ExpectedBudget(s, p, scale) && Eq((float)scale, c.Id == "Custom" ? 2f : RaidCatalog.Difficulty(c.Id).Scale);
                all &= ok;
                rows.Add($"{c.Id}#{seed}:{(p == null ? "无" : $"L{p.Level}/预算{p.Budget}")}");
            }
            Expect(all, "D1 第一次普通突袭在任何难度下都固定 1 级、规模很小（暴露一笔越过 90 的 3 级触发也一样；预算 = 100 × 难度规模 × 间隔系数 × 0.6）：" + string.Join("，", rows) + "（4 个世界、3 个种子，B25）");
        }

        private static void CheckFirstRaidAfterCancel()
        {
            CampaignState s = NewWorld(6321);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionFoundry, 95f);
            RaidPlanRecord first = Plans(s).FirstOrDefault(x => !x.Exempt);
            bool scheduled = first != null && RunUntil(() => first.State >= RaidDirectorService.StateScheduled, 200) && first.State < RaidDirectorService.StateWarned && D(s).LastArrivalTick >= 0;
            DifficultyService.Change(s, P("Builder"), out DifficultyChangeRecord r1);
            int rcAfterCancel = D(s).RaidCount;
            bool cancelled = first != null && first.State == RaidDirectorService.StateCancelled && first.EndReason == RaidDirectorService.EndCancelled && D(s).RaidCount == 0
                             && r1 != null && r1.CancelledRaids == 1;
            WorldSimulation.StepMany(2);
            bool cleaned = Plans(s).Length == 0 && D(s).History.Any(h => h.PlanId == first?.PlanId && h.EndReason == RaidDirectorService.EndCancelled)
                           && D(s).LastArrivalTick < 0;
            DifficultyService.Change(s, P("Standard"), out _);
            // 阈值位已经置上（不追溯）；暴露仍在 90 以上：等 4 级周期触发——它仍是“第一次普通突袭”，固定 1 级。
            long l4 = D(s).Level4NextTick;
            bool skipped = l4 > GameClock.Ticks && SkipIdle(s, l4 - 2);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            RaidPlanRecord next = Plans(s).FirstOrDefault(x => !x.Exempt);
            bool level1 = next != null && next.FirstRaid && next.Level == 1 && next.Triggers.Contains(RaidCatalog.TriggerExposure);
            Expect(scheduled && cancelled && cleaned && skipped && level1,
                $"D2 第一波（还没预警）被改成建造者取消：计划取消、进历史（取消）、第一次突袭的名额还回去（取消后 RaidCount {rcAfterCancel}）、最短间隔不再参照它；改回标准后，暴露 90 以上的 4 级周期触发的下一波仍是“第一次突袭” → 固定 1 级（{next?.Level} 级）");
        }

        // ── E 游戏中途修改（真 UXML 难度面板）───────────────────────────────────────

        private static void CheckMidGamePanel()
        {
            CampaignState s = NewWorld(6331);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord p = Plans(s).FirstOrDefault(x => !x.Exempt);
            int b0 = p?.Budget ?? -1;
            int rev0 = p?.Revision ?? -1;
            VisualElement root = F.MountUxml(PanelUxml, out GameObject go);
            DifficultyPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                DifficultyPanelUIToolkit panel = go.AddComponent<DifficultyPanelUIToolkit>();
                panel.BindView(root);
                DifficultyPanelUIToolkit.Open();
                DifficultyPickerView pk = panel.Picker;
                bool opened = DifficultyPanelUIToolkit.IsOpen && panel.PanelVisible && pk.PresetCount == 4 && pk.Choice.Id == "Standard"
                              && pk.PresetButton("Standard").text.StartsWith("▸", StringComparison.Ordinal) && panel.HistoryRowCount == 2
                              && panel.HistoryText(0) == GameText.Format("ui.difficulty.history_start", DifficultyService.Name("Standard"))
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DifficultyFirstOpen) && !GameText.ContainsMarker(panel.CurrentText + pk.DescriptionText);
                // E1：点“严酷”→“应用修改”→ 二次确认（写明记入存档、还没预警的突袭重新编成）→ 确认。
                int notes0 = NotifyCount("difficulty_changed");
                bool picked = ClickButton(pk.PresetButton("Harsh")) && pk.Choice.Id == "Harsh" && s.DifficultyId == "Standard";
                bool asked = ClickButton(panel.ApplyButton) && UiConfirmDialog.IsOpen
                             && UiConfirmDialog.Current.Lines.Contains(GameText.Get("ui.difficulty.confirm_record"))
                             && UiConfirmDialog.Current.Lines.Contains(GameText.Get("ui.difficulty.confirm_pending")) && UiConfirmDialog.Current.Consequences.Count == 0;
                UiConfirmDialog.Confirm();
                RaidDifficultyState d = DifficultyService.StateOf(s);
                DifficultyChangeRecord rec = d.Changes.LastOrDefault();
                bool changed = s.DifficultyId == "Harsh" && d.ChangeCount == 1 && rec != null && rec.FromId == "Standard" && rec.ToId == "Harsh" && rec.Tick == GameClock.Ticks
                               && Eq(rec.ToScale, 1.3f) && p != null && p.Budget == Scaled(b0, ScaleRatio("Standard", "Harsh")) && p.Budget != b0 && p.Revision > rev0 && p.Level == 1
                               && NotifyCount("difficulty_changed") == notes0 + 1 && LastNotify("difficulty_changed").Contains(DifficultyService.Name("Harsh"))
                               && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DifficultyFirstChanged)
                               && panel.HistoryRowCount == 2 && panel.HistoryText(1) == DifficultyService.HistoryRow(rec) && panel.CurrentText.Contains(DifficultyService.Name("Harsh"))
                               && panel.FeedbackText == GameText.Format("ui.difficulty.applied", DifficultyService.Label(s));
                Expect(opened && picked && asked && changed,
                    $"E1 难度面板（真 UXML、按钮真点击）：打开时当前“标准”、修改记录只有开局；点“严酷”只改选择不改存档；“应用修改”弹二次确认（写明记入存档 / 还没预警的突袭重新编成）；" +
                    $"确认后当前难度 = 严酷、记一条修改（第 {rec?.Tick} 步，标准 → 严酷）、还没预警的第一次突袭按严酷重新编成（预算 {b0} → {p?.Budget} = 原预算 × 1.3，仍是 1 级）、发“难度修改”通知、引导钩子；面板显示“{panel.HistoryText(1)}”");

                // E2：与当前相同 → 不弹框、不记录。
                bool same = ClickButton(panel.ApplyButton) && !UiConfirmDialog.IsOpen && panel.FeedbackText == GameText.Get("ui.difficulty.unchanged") && d.ChangeCount == 1;
                // E3：选建造者 → 确认框写明会取消 1 波普通突袭；点取消 → 什么都不变。
                ClickButton(pk.PresetButton("Builder"));
                bool warnCancel = ClickButton(panel.ApplyButton) && UiConfirmDialog.IsOpen
                                  && UiConfirmDialog.Current.Consequences.Contains(GameText.Format("ui.difficulty.confirm_cancel", 1));
                UiConfirmDialog.Cancel();
                bool untouched = s.DifficultyId == "Harsh" && d.ChangeCount == 1 && p.State < RaidDirectorService.StateWarned;
                Expect(same && warnCancel && untouched,
                    "E2 选的与当前相同时“应用修改”只写“没有修改”（不弹框、不记录）；E3 选建造者时确认框写明“还没发出预警的 1 波普通突袭会取消”，点取消什么都不变（仍是严酷、计划还在）");

                // E4：确认建造者 → 还没预警的普通突袭取消；通知写明取消了几波。
                ClickButton(panel.ApplyButton);
                UiConfirmDialog.Confirm();
                bool builder = s.DifficultyId == "Builder" && p.State == RaidDirectorService.StateCancelled && d.ChangeCount == 2 && d.Changes.Last().CancelledRaids == 1
                               && LastNotify("difficulty_changed") == GameText.Format("ui.difficulty.notify_cancelled", DifficultyService.Name("Harsh"), DifficultyService.Name("Builder"), 1)
                               && d.EverLeftStart && !DifficultyService.KeptThroughout(s, "Standard");
                WorldSimulation.StepMany(2);
                bool gone = Plans(s).All(x => x.Trigger == RaidCatalog.TriggerStory);
                Expect(builder && gone, $"E4 确认改成建造者：还没预警的普通突袭取消（通知“{LastNotify("difficulty_changed")}”），之后只剩剧情突袭；离开过开局难度（“全程标准”不再成立）");

                // E5：自定义滑条（真滑条拖动：value 触发回调）按步长取整、钳进范围；应用后导演按自定义倍率。
                bool customShown = ClickButton(pk.PresetButton("Custom")) && pk.CustomVisible && pk.Choice.Id == "Custom" && Eq(pk.Choice.Scale, 0.5f);
                pk.FrequencySlider.value = 1.73f;
                pk.ScaleSlider.value = 0.31f;
                pk.WarningSlider.value = 2.5f;
                bool snapped = Eq(pk.Choice.Frequency, 1.7f) && Eq(pk.Choice.Scale, 0.5f) && Eq(pk.Choice.Warning, 2f) && Eq(pk.FrequencySlider.value, 1.7f)
                               && pk.FrequencyLabelText == GameText.Format("ui.difficulty.slider.frequency", "1.7") && pk.DescriptionText.Contains(GameText.Format("difficulty.line.frequency", "1.7"));
                ClickButton(panel.ApplyButton);
                UiConfirmDialog.Confirm();
                bool applied = s.DifficultyId == "Custom" && Eq(d.CustomFrequency, 1.7f) && Eq(d.CustomScale, 0.5f) && Eq(d.CustomWarning, 2f)
                               && Eq(DifficultyService.Frequency(s), 1.7f) && Eq(DifficultyService.Scale(s), 0.5f) && !DifficultyService.StoryOnly(s)
                               && RaidDirectorService.MinWarningTicks(s) == RaidDirectorService.HourTicks(1.5 * 2.0)
                               && panel.CurrentText.Contains(GameText.Format("ui.difficulty.custom_values", DifficultyService.Name("Custom"), "1.7", "0.5", "2"));
                // 自定义下暴露触发照常（自定义不是“只有剧情”），第一次突袭仍固定 1 级、规模 × 0.5。
                Raise(s, CampaignExposureLedger.FactionFoundry, 61f);
                RaidPlanRecord q = Plans(s).FirstOrDefault(x => !x.Exempt && x.State < RaidDirectorService.StateEnded);
                bool raid = q != null && q.FirstRaid && q.Level == 1 && q.Budget == ExpectedBudget(s, q, 0.5);
                Expect(customShown && snapped && applied && raid,
                    $"E5 自定义：点“自定义”出现三个滑条（从当前选择出发）；拖到 1.73 / 0.31 / 2.5 → 按步长与范围取 1.7 / 0.5 / 2；应用后频率 ×1.7、规模 ×0.5、最短预警 ×2（{RaidDirectorService.MinWarningTicks(s)} 步）；" +
                    $"自定义下暴露照常引来突袭，下一次仍是“第一次突袭”1 级（预算 {q?.Budget}）；顶部写“{panel.CurrentText}”");
                // 预设按钮重建（每次打开面板都会重建）：旧按钮的提示先解绑，UiTooltip 的静态绑定表不增长（复审 P2）。
                int tips0 = UiTooltip.BindingCount;
                pk.BuildPresets();
                pk.BuildPresets();
                bool noLeak = UiTooltip.BindingCount == tips0 && pk.PresetCount == 4;
                DifficultyPanelUIToolkit.Close();
                bool closed = !DifficultyPanelUIToolkit.IsOpen && !panel.PanelVisible;
                Expect(closed && noLeak, $"E6 关闭难度面板（Esc 栈 / 关闭按钮同一出口）；预设按钮重建两次后提示绑定数不变（{tips0} → {UiTooltip.BindingCount}）");
            }
            finally
            {
                UiConfirmDialog.DiscardAll();
                DifficultyPanelUIToolkit.Close();
                DifficultyPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        private static void CheckWarnedUntouchedAndTimers()
        {
            CampaignState s = NewWorld(6341);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionFoundry, 95f);
            RaidPlanRecord p = Plans(s).FirstOrDefault(x => !x.Exempt);
            // 4 级周期与骚扰计时：频率 ×1.3 → 剩余时间 ÷1.3。
            long now = GameClock.Ticks;
            long l4Rem = D(s).Level4NextTick - now;
            long hRem = D(s).HarassNextTick - now;
            DifficultyService.Change(s, P("Harsh"), out _);
            long l4After = D(s).Level4NextTick - now;
            long hAfter = D(s).HarassNextTick - now;
            bool timers = l4Rem > 0 && hRem > 0 && Math.Abs(l4After - Math.Round(l4Rem / 1.3)) <= 1 && Math.Abs(hAfter - Math.Round(hRem / 1.3)) <= 1;
            // 已发预警的计划：改难度不重新编成、不取消。
            bool warned = p != null && RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 200) && SkipIdle(s, p.WarnTick - 2)
                          && RunUntil(() => p.State >= RaidDirectorService.StateWarned, 60);
            int budget = p?.Budget ?? -1;
            int rev = p?.Revision ?? -1;
            DifficultyService.Change(s, P("Builder"), out DifficultyChangeRecord r2);
            bool kept = warned && p.State >= RaidDirectorService.StateWarned && p.State != RaidDirectorService.StateCancelled && p.Budget == budget && p.Revision == rev && r2.CancelledRaids == 0;
            Expect(timers && kept,
                $"E7 频率变了：4 级周期剩余 {l4Rem} → {l4After} 步、骚扰剩余 {hRem} → {hAfter} 步（÷1.3）；E8 已经发出预警的突袭改难度（严酷 → 建造者）不重新编成、不取消（预算 {budget}，状态 {p?.State}）");
        }

        private static void CheckHistoryAndKept()
        {
            // 不载入世界：只测记录与判定（导演没有计划）。
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            CampaignState s = CampaignState.CreateNew("fgdiff-hist", "Harsh", 6351);
            DifficultyService.EnsureState(s);
            bool keptAtStart = DifficultyService.KeptThroughout(s, "Harsh") && !DifficultyService.KeptThroughout(s, "Standard");
            // 改成数值相同的自定义也算离开严酷；改回严酷不恢复。
            DifficultyService.Change(s, new DifficultyChoice("Custom", 1.3f, 1.3f, 0.8f), out _);
            DifficultyService.Change(s, P("Harsh"), out _);
            bool lost = !DifficultyService.KeptThroughout(s, "Harsh") && DifficultyService.StateOf(s).EverLeftStart && DifficultyService.StateOf(s).StartDifficultyId == "Harsh";
            for (int i = 0; i < 40; i++)
            {
                GameClock.SkipForTests(s, GameClock.StepHz);
                DifficultyService.Change(s, P(i % 2 == 0 ? "Standard" : "Harsh"), out _);
            }
            RaidDifficultyState d = DifficultyService.StateOf(s);
            bool trimmed = d.Changes.Length == 32 && d.ChangeCount == 42 && d.Changes.Last().ToId == "Harsh" && d.Changes.First().Tick < d.Changes.Last().Tick;
            var e = DifficultyService.Change(s, P("Harsh"), out DifficultyChangeRecord none);
            bool unchanged = e == DifficultyChangeResult.Unchanged && none == null && d.ChangeCount == 42;
            bool noCampaign = DifficultyService.Change(null, P("Harsh"), out _) == DifficultyChangeResult.NoCampaign;
            // 认不出的难度 ID → 按标准（存档被改坏 / 未来版本的 ID）。
            var odd = new CampaignState { DifficultyId = "Nightmare" };
            bool fallback = DifficultyService.EffectiveId(odd) == "Standard" && Eq(DifficultyService.Scale(odd), 1f) && DifficultyService.Normalize(new DifficultyChoice("Nightmare")).Id == "Standard";
            Expect(keptAtStart && lost && trimmed && unchanged && noCampaign && fallback,
                $"E9 “全程保持”判定（FG15 FGR-SYS-081）：严酷开局 = 全程严酷；改成数值相同的自定义再改回严酷 → 不再成立（离开过开局难度永久记下）；修改记录最多 32 条（共 {d.ChangeCount} 次，更早的只留计数）；" +
                "与当前相同返回“未改变”、没有战役返回“没有战役”；认不出的难度 ID 按标准");
        }

        // ── E10～E15 中途修改：预算按规模比例缩放、建造者取消口径、最短间隔参照（复审修复）──────────────

        /// <summary>导演缩放预算的同一算式（float 倍率提升成 double 后相除，避免 1.3 / 1.3f 的取整差）。</summary>
        private static double ScaleRatio(string from, string to) => RaidCatalog.Difficulty(to).Scale / (double)RaidCatalog.Difficulty(from).Scale;

        private static int Scaled(int budget, double ratio) => Math.Max(1, (int)Math.Round(budget * ratio));

        private static void CheckScheduledRescale()
        {
            CampaignState s = NewWorld(6361);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord p = Plans(s).FirstOrDefault(x => !x.Exempt);
            bool sched = p != null && RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 200) && p.State == RaidDirectorService.StateScheduled;
            int b0 = p?.Budget ?? -1;
            // 场景前提：排定后“距上一波抵达”的间隔系数已变回 1（它自己的抵达记进了参照），从零重算的预算比开局算的小——旧实现在这里会把突袭改小。
            int fromScratch = p != null ? ExpectedBudget(s, p, 1.0) : -1;
            bool premise = sched && RaidDirectorService.IntervalCoef(D(s), p.CreatedTick) < 1.0001 && fromScratch < b0;
            int rev0 = p?.Revision ?? -1;
            DifficultyService.Change(s, P("Harsh"), out _);
            int bH = p?.Budget ?? -1;
            bool harsh = sched && bH == Scaled(b0, ScaleRatio("Standard", "Harsh")) && bH > b0 && p.Revision > rev0 && p.Level == 1 && p.State == RaidDirectorService.StateScheduled;
            // 只改预警（规模、频率都与严酷相同的自定义）：预算、编成、预报都不动。
            int rev1 = p?.Revision ?? -1;
            int units1 = p?.UnitTotal ?? -1;
            DifficultyService.Change(s, new DifficultyChoice("Custom", 1.3f, 1.3f, 1.5f), out _);
            bool warnOnly = p != null && s.DifficultyId == "Custom" && p.Budget == bH && p.Revision == rev1 && p.UnitTotal == units1;
            DifficultyService.Change(s, P("Standard"), out _);
            bool back = p != null && Math.Abs(p.Budget - b0) <= 1;
            Expect(premise && harsh && warnOnly && back,
                $"E10 已排定、还没预警的突袭改难度：预算按“新规模 ÷ 旧规模”缩放，不从零重算（间隔加成保留）——标准 {b0}（从零重算会是 {fromScratch}）→ 严酷 {bH}（×1.3）；" +
                $"只改预警的自定义：预算 / 编成 / 预报序号都不变；改回标准 {p?.Budget}（取整误差 ≤ 1）");
        }

        private static void CheckMergedRescaleAndBuilder()
        {
            CampaignState s = NewWorld(6171);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionSilent, 31f);
            RaidPlanRecord normal = LatestPlan(s);
            RaidDirectorService.RequestStoryRaid(s, "foundry", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord story = LatestPlan(s);
            int nb = normal?.Budget ?? -1;
            int sb = story?.Budget ?? -1;
            bool merged = normal != null && story != null && normal != story
                          && RunUntil(() => normal.State >= RaidDirectorService.StateScheduled && story.State >= RaidDirectorService.StateScheduled, 200)
                          && normal.State == RaidDirectorService.StateMerged && normal.MergedInto == story.PlanId && story.State == RaidDirectorService.StateScheduled
                          && story.Budget == nb + sb && story.AbsorbedBudget == nb && story.AbsorbedIds.Length == 1 && story.AbsorbedIds[0] == normal.PlanId;
            int rc0 = D(s).RaidCount;
            // E11：标准 → 严酷：合并后的整波（剧情 + 吸收的普通）一起 ×1.3，吸收的那部分不丢。
            DifficultyService.Change(s, P("Harsh"), out _);
            double rH = ScaleRatio("Standard", "Harsh");
            int kH = story?.Budget ?? -1;
            int aH = story?.AbsorbedBudget ?? -1;
            bool harsh = merged && kH == Scaled(nb + sb, rH) && aH == Scaled(nb, rH) && story.AbsorbedIds.Length == 1;
            // E12：被吸收的普通计划进历史后改成建造者：确认框计数算上它；剧情一方去掉普通那部分、按建造者规模缩放；名额归还；它改记取消、不再算最短间隔的参照。
            WorldSimulation.StepMany(3);
            bool inHistory = RaidDirectorService.FindPlan(s, normal?.PlanId) == null && D(s).History.Any(h => h.PlanId == normal?.PlanId && h.EndReason == RaidDirectorService.EndMerged);
            int ask = DifficultyService.PendingNormalRaidsToCancel(s, P("Builder"));
            DifficultyService.Change(s, P("Builder"), out DifficultyChangeRecord rb);
            int expect = Scaled(kH - aH, ScaleRatio("Harsh", "Builder"));
            bool builder = merged && inHistory && ask == 1 && rb != null && rb.CancelledRaids == 1 && story.State == RaidDirectorService.StateScheduled
                           && story.Budget == expect && story.AbsorbedBudget == 0 && story.AbsorbedIds.Length == 0
                           && story.Triggers.Length == 1 && story.Triggers[0] == RaidCatalog.TriggerStory && D(s).RaidCount == rc0 - 1
                           && D(s).History.Any(h => h.PlanId == normal.PlanId && h.EndReason == RaidDirectorService.EndCancelled)
                           && D(s).LastArrivalTick < 0 && D(s).LastAnyArrivalTick == story.ArrivalTick;
            Expect(merged && harsh,
                $"E11 剧情突袭吸收普通突袭（{sb} + {nb}）后标准 → 严酷：整波 {kH} = ({sb}+{nb}) × 1.3，吸收部分 {aH} 保留（旧实现会缩回只剩剧情那份 × 1.3）");
            Expect(builder,
                $"E12 再改成建造者：确认框写“{ask} 波”、修改记录取消 {rb?.CancelledRaids} 波；剧情一方去掉普通部分后按 ×0.5/1.3 缩放 = {story?.Budget}（期望 {expect}），触发只剩剧情；" +
                $"第一次突袭名额归还（RaidCount {rc0} → {D(s).RaidCount}）；被吸收的计划在历史里改记取消，最短间隔的普通参照清空（{D(s).LastArrivalTick}）");
        }

        private static void CheckExemptNonStoryCancelled()
        {
            CampaignState s = NewWorld(6371);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionFoundry, 10f); // 有阵营被刺激（静默夜 / 巨构由被刺激最多的阵营发动），但不越过 30
            RaidDirectorService.OnSilentNightStarted(s);
            WorldSimulation.StepMany(2);
            RaidPlanRecord night = LatestPlan(s);
            bool nightPlanned = night != null && night.Exempt && night.Trigger == RaidCatalog.TriggerSilentNight && night.State < RaidDirectorService.StateWarned;
            int rc0 = D(s).RaidCount;
            int ask = DifficultyService.PendingNormalRaidsToCancel(s, P("Builder"));
            DifficultyService.Change(s, P("Builder"), out DifficultyChangeRecord r1);
            bool nightCancelled = nightPlanned && ask == 1 && r1 != null && r1.CancelledRaids == 1 && night.State == RaidDirectorService.StateCancelled
                                  && night.EndReason == RaidDirectorService.EndCancelled && D(s).RaidCount == rc0;
            DifficultyService.Change(s, P("Standard"), out _);
            RaidDirectorService.OnMegastructureStageCompleted(s);
            WorldSimulation.StepMany(2);
            RaidPlanRecord mega = LatestPlan(s);
            bool megaPlanned = mega != null && mega != night && mega.Exempt && mega.Trigger == RaidCatalog.TriggerMegastructure && mega.State < RaidDirectorService.StateWarned;
            DifficultyService.Change(s, P("Builder"), out DifficultyChangeRecord r2);
            bool megaCancelled = megaPlanned && r2 != null && r2.CancelledRaids == 1 && mega.State == RaidDirectorService.StateCancelled;
            WorldSimulation.StepMany(2);
            bool onlyStory = Plans(s).All(x => x.Trigger == RaidCatalog.TriggerStory);
            Expect(nightCancelled && megaCancelled && onlyStory,
                $"E13 负向“建造者只有剧情突袭”：标准下排好的静默夜突袭（不受最短间隔、但不是剧情）还没预警时改成建造者 → 确认框计 {ask} 波、取消（{night?.State}），" +
                $"巨构阶段突袭同样取消（{mega?.State}）；与新开建造者局跳过这两类触发同一口径；第一次突袭名额不受影响（豁免类本来不占）");
        }

        private static void CheckAnchorsAndPostgame()
        {
            CampaignState s = NewWorld(6391);
            PassGrace(s);
            // “读一个存档”：上一波普通突袭在途中被全歼（历史里没有到达步，只有排定的抵达步），后日谈已开启。
            RaidDirectorState d = D(s);
            long destroyedArrival = GameClock.Ticks - Day(0.1);
            d.History = d.History.Concat(new[]
            {
                new RaidHistoryRecord
                {
                    PlanId = "raid-probe-destroyed", Trigger = RaidCatalog.TriggerExposure, Faction = CampaignExposureLedger.FactionFoundry, Level = 1, Units = 3,
                    WarnTick = destroyedArrival - Day(0.3), ArrivedTick = -1, ArrivalTick = destroyedArrival, EndTick = destroyedArrival - Day(0.05),
                    EndReason = RaidDirectorService.EndDestroyed,
                },
            }).ToArray();
            d.LastArrivalTick = destroyedArrival;
            d.LastAnyArrivalTick = destroyedArrival;
            bool postgame = s.Progress != null;
            if (postgame)
            {
                s.Progress.IsPostgame = true;
            }
            int pg0 = d.PostgameCount;
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord p = Plans(s).FirstOrDefault(x => !x.Exempt);
            bool planned = postgame && p != null && p.PostgameIndex == pg0 && d.PostgameCount == pg0 + 1
                           && RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 200) && p.State == RaidDirectorService.StateScheduled && p.IntervalBaseTick == destroyedArrival;
            DifficultyService.Change(s, P("Builder"), out DifficultyChangeRecord r);
            bool anchors = planned && r != null && r.CancelledRaids == 1 && p.State == RaidDirectorService.StateCancelled
                           && d.LastArrivalTick == destroyedArrival && d.LastAnyArrivalTick == destroyedArrival;
            bool refunded = planned && d.PostgameCount == pg0;
            if (postgame)
            {
                s.Progress.IsPostgame = false;
            }
            Expect(anchors && refunded,
                $"E14 取消后重算最短间隔的参照：途中被全歼的上一波普通突袭（历史只有排定的抵达步 {destroyedArrival}）仍算参照（重算后 {d.LastArrivalTick} / {d.LastAnyArrivalTick}），不让下一波提前；" +
                $"E15 取消的后日谈计划归还清洗序号（{pg0} → {pg0 + 1} → {d.PostgameCount}），下一次清洗不多算一档");
        }

        // ── F 存读档 ─────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class TestEnvelope
        {
            public int SchemaVersion;
            public int ContentVersion;
            public string Checksum;
            public string WrittenAtUtc;
            public string ProductVersion;
            public string CardJson;
            public string PayloadJson;
        }

        private static CampaignState RestoreSlot(out string fail)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                fail = rr.Message;
                return null;
            }
            fail = null;
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            return rr.State;
        }

        private static void EditPayload(Func<string, string> edit)
        {
            string path = CampaignSaveService.SlotPath(Slot);
            TestEnvelope env = JsonUtility.FromJson<TestEnvelope>(File.ReadAllText(path));
            env.PayloadJson = edit(env.PayloadJson);
            env.Checksum = CampaignSaveService.ComputeChecksum(env.PayloadJson, env.CardJson);
            File.WriteAllText(path, JsonUtility.ToJson(env));
        }

        /// <summary>去掉正文里 RaidState 的 "Difficulty":{...}（旧档没有难度域）。</summary>
        private static string StripDifficulty(string json, out bool stripped)
        {
            stripped = false;
            int at = json.IndexOf("\"Difficulty\":{", StringComparison.Ordinal);
            if (at < 0)
            {
                return json;
            }
            int depth = 0;
            bool inString = false;
            for (int i = at + "\"Difficulty\":".Length; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && --depth == 0)
                {
                    int from = json[at - 1] == ',' ? at - 1 : at;
                    stripped = true;
                    return json.Remove(from, i + 1 - from);
                }
            }
            return json;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(6361, P("Harsh"));
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            DifficultyService.Change(s, new DifficultyChoice("Custom", 1.2f, 0.7f, 1.6f), out _);
            WorldSimulation.StepMany(GameClock.StepHz);
            string before = DifficultyService.Snapshot(s);
            string dirBefore = RaidDirectorService.Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(Slot);
            CampaignState l = RestoreSlot(out string fail);
            string loaded = l != null ? DifficultyService.Snapshot(l) : "读档失败：" + fail;
            string dirLoaded = l != null ? RaidDirectorService.Snapshot(l) : string.Empty;
            bool card = meta.DifficultyId == "Custom" && CampaignSlotText.DifficultyName(meta.DifficultyId) == GameText.Get("difficulty.custom.name");
            bool effects = l != null && Eq(DifficultyService.Frequency(l), 1.2f) && Eq(DifficultyService.Scale(l), 0.7f) && Eq(DifficultyService.Warning(l), 1.6f)
                           && DifficultyService.StateOf(l).StartDifficultyId == "Harsh" && DifficultyService.StateOf(l).Changes.Length == 1 && !DifficultyService.KeptThroughout(l, "Harsh");
            Expect(save.Success && loaded == before && dirLoaded == dirBefore && card && effects && before.Contains("|C"),
                $"F1 真文件存读档：当前难度、开局难度、自定义三个倍率、修改记录（时刻 / 前后 / 取消数）、离开过开局难度逐字段一致（{Short(before)}）；导演按自定义倍率接着工作；存档卡写“{CampaignSlotText.DifficultyName(meta.DifficultyId)}”" +
                (loaded == before ? string.Empty : $"\n存前：{before}\n读后：{loaded}") + (dirLoaded == dirBefore ? string.Empty : "\n导演快照不一致"));

            // F2 旧档（正文没有难度域）：读档补成空域——开局难度按当前难度、没有修改记录、自定义倍率 ×1；之后可以正常修改。
            bool stripped = false;
            EditPayload(json => StripDifficulty(json, out stripped));
            CampaignState legacy = RestoreSlot(out string fail2);
            RaidDifficultyState ld = DifficultyService.StateOf(legacy);
            bool legacyOk = stripped && legacy != null && ld != null && ld.StartDifficultyId == legacy.DifficultyId && ld.Changes.Length == 0 && ld.ChangeCount == 0
                            && Eq(ld.CustomFrequency, 1f) && Eq(DifficultyService.Frequency(legacy), 1f)
                            && DifficultyService.Change(legacy, P("Standard"), out _) == DifficultyChangeResult.Changed && ld.Changes.Length == 1;
            Expect(legacyOk, $"F2 旧档（没有难度域）：读档补成空域不报错——开局难度按当前（{ld?.StartDifficultyId}）、没有修改记录、自定义倍率 ×1；之后照常修改并记录" + (fail2 != null ? "；读档失败：" + fail2 : string.Empty));

            // F3 坏值：存档里的自定义倍率被改成 9 / -3 → 读档钳回滑条范围（上限 2）/ 补成 1。
            WorldSimulation.SyncAllForSave();
            DifficultyService.Change(legacy, new DifficultyChoice("Custom", 1.5f, 1.5f, 1.5f), out _);
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            EditPayload(json => Regex.Replace(Regex.Replace(json, "\"CustomFrequency\":[-0-9.eE]+", "\"CustomFrequency\":9.0"), "\"CustomScale\":[-0-9.eE]+", "\"CustomScale\":-3.0"));
            CampaignState badS = RestoreSlot(out string fail3);
            RaidDifficultyState bd = DifficultyService.StateOf(badS);
            bool clamped = badS != null && bd != null && Eq(bd.CustomFrequency, 2f) && Eq(bd.CustomScale, 1f) && Eq(bd.CustomWarning, 1.5f) && Eq(DifficultyService.Frequency(badS), 2f);
            Expect(clamped, $"F3 坏值：存档里的自定义频率 9 → 钳回上限 ×{bd?.CustomFrequency}；规模 -3 → 补成 ×{bd?.CustomScale}；其余不变" + (fail3 != null ? "；读档失败：" + fail3 : string.Empty));
        }

        // ── G 暂停 / 倍速 / 观察 ───────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, null, observe);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord p = LatestPlan(s);
            RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            SkipIdle(s, p.WarnTick - GameClock.StepHz * 5);
            pausedHeld = true;
            long changeTick = GameClock.Ticks;
            if (pauseFirst)
            {
                // 暂停中打开难度面板修改：按暂停那一步记录；暂停期间导演不动。
                GameClock.SetPaused(true);
                DifficultyService.Change(s, P("Harsh"), out DifficultyChangeRecord r);
                string p0 = RaidDirectorService.Snapshot(s) + DifficultyService.Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = RaidDirectorService.Snapshot(s) + DifficultyService.Snapshot(s) == p0 && r != null && r.Tick == changeTick;
                GameClock.SetPaused(false);
            }
            else
            {
                DifficultyService.Change(s, P("Harsh"), out _);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 160;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 1200)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return RaidDirectorService.Snapshot(s) + " ## " + DifficultyService.Snapshot(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(6371, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{Short(snap)}\n参照：{Short(reference)}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains("Harsh") && reference.Contains("|G"),
                "G1 暂停中修改难度：按暂停那一步记录、暂停 120 帧导演与难度状态都不动；0.5x / 1x / 2x / 3x 接着跑 160 游戏秒（预警 → 集结 → 出发 → 行进，严酷难度）逐字段一致" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(6372, true, 1f, false, out _);
            string unseen = RunScenario(6372, false, 1f, false, out _);
            Expect(seen == unseen && seen.Contains("|G"), "G2 同一次难度修改与之后的突袭，观察与不观察家园逐字段一致（FGR-BASE-021）" + (seen == unseen ? string.Empty : $"\n观察：{Short(seen)}\n不观察：{Short(unseen)}"));
        }

        // ── H 攻城展开的敌人耐久 / 伤害 ─────────────────────────────────────────────

        private static Vector2 CoreCenter(CampaignState s)
        {
            HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
            return new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        }

        /// <summary>离核心 <paramref name="dist"/> 格、敌方可走的一点（按方向依次试；B25 不写死坐标）。</summary>
        private static Vector2 OutsidePoint(CampaignState s, float dist)
        {
            Vector2 c = CoreCenter(s);
            for (int k = 0; k < 16; k++)
            {
                float ang = k * 22.5f * Mathf.Deg2Rad;
                var p = new Vector2(c.x + Mathf.Cos(ang) * dist, c.y + Mathf.Sin(ang) * dist);
                GridCell g = SiegeService.NearestPassable(NavService.CellOf(p.x, p.y), 6);
                if (NavService.PassableNow(g.X, g.Y, NavConst.ClassHostile))
                {
                    return new Vector2(g.X, g.Y);
                }
            }
            return c + new Vector2(dist, 0f);
        }

        /// <summary>测试捷径：一支已经到达的突袭队伍（正式派出 → 行进 → 到达由 FgRaidDirectorSelfCheck / FgSiegeSelfCheck 覆盖），下一步攻城服务按编成展开。</summary>
        private static TransitGroupRecord Arrive(CampaignState s, Vector2 at, string faction, string unitId, int count)
        {
            GridCell core = HomeGridService.CorePivot(s);
            Vector2 o = at + (at - CoreCenter(s)).normalized * 60f;
            TransitGroupRecord g = WorldTransitSystem.Dispatch(s, TransitGroupKind.Raid, faction, count, o.x, o.y, core.X, core.Y);
            g.Faction = faction;
            g.UnitIds = new[] { unitId };
            g.UnitCounts = new[] { count };
            g.EliteCounts = new[] { 0 };
            g.TargetKind = RaidDirectorService.TargetHome;
            g.PosX = at.x;
            g.PosY = at.y;
            g.RouteX = new[] { (int)Mathf.Round(at.x) };
            g.RouteY = new[] { (int)Mathf.Round(at.y) };
            g.RouteIndex = 1;
            g.RouteState = WorldTransitSystem.RouteFollowing;
            g.State = TransitGroupState.Arrived;
            g.ArrivedAtTick = GameClock.Ticks;
            WorldSimulation.StepMany(2);
            return g;
        }

        /// <summary>测试捷径：一支还在行进、离家园 40 格的突袭队伍被己方机器拦截（与 SiegeService.Tick 里同一个入口 <see cref="SiegeService.Intercept"/>）。</summary>
        private static (int Count, float MaxHp, float Damage) InterceptUnfolded(string difficulty)
        {
            NewWorld(6382, P(difficulty));
            CampaignState cs = CampaignSession.Current;
            Vector2 at = OutsidePoint(cs, 40f);
            GridCell core = HomeGridService.CorePivot(cs);
            Vector2 o = at + (at - CoreCenter(cs)).normalized * 60f;
            TransitGroupRecord g = WorldTransitSystem.Dispatch(cs, TransitGroupKind.Raid, "foundry", 3, o.x, o.y, core.X, core.Y);
            g.Faction = "foundry";
            g.UnitIds = new[] { "foundry.armorbot" };
            g.UnitCounts = new[] { 3 };
            g.EliteCounts = new[] { 0 };
            g.TargetKind = RaidDirectorService.TargetHome;
            g.PosX = at.x;
            g.PosY = at.y;
            CombatSite site = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            if (site == null || SiegeService.Intercept(cs, site, g) <= 0)
            {
                return (0, 0f, 0f);
            }
            return ReadRaiders(site, g);
        }

        private static (int Count, float MaxHp, float Damage) Unfolded(string difficulty)
        {
            NewWorld(6381, P(difficulty));
            CampaignState cs = CampaignSession.Current;
            TransitGroupRecord g = Arrive(cs, OutsidePoint(cs, 40f), "foundry", "foundry.armorbot", 3);
            CombatSite site = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            return ReadRaiders(site, g);
        }

        private static (int Count, float MaxHp, float Damage) ReadRaiders(CombatSite site, TransitGroupRecord g)
        {
            var ids = new List<int>();
            site?.SiegeRaiderIds(SiegeService.KeyOf(g), ids);
            float hp = 0f;
            float dmg = 0f;
            foreach (int id in ids)
            {
                if (site.TryGetSiegeRaider(id, out _, out _, out _, out _, out float maxHp) && site.Kernel.TryGetUnit(id, out CombatUnitView v)
                    && site.Kernel.TryGetWeapon(v.Weapon, out CombatWeapon w))
                {
                    hp = maxHp;
                    dmg = w.Damage;
                    break;
                }
            }
            return (ids.Count, hp, dmg);
        }

        private static void CheckEnemyStats()
        {
            (int nS, float hpS, float dmgS) = Unfolded("Standard");
            (int nH, float hpH, float dmgH) = Unfolded("Harsh");
            (int nB, float hpB, float dmgB) = Unfolded("Builder");
            (int nC, float hpC, float dmgC) = Unfolded("Custom");
            bool ok = nS == 3 && nH == 3 && nB == 3 && nC == 3 && hpS > 0f && dmgS > 0f
                      && Math.Abs(hpH / hpS - 1.2f) < 1e-3f && Math.Abs(dmgH / dmgS - 1.2f) < 1e-3f
                      && Math.Abs(hpB / hpS - 0.8f) < 1e-3f && Math.Abs(dmgB / dmgS - 0.8f) < 1e-3f
                      && Math.Abs(hpC - hpS) < 1e-3f && Math.Abs(dmgC - dmgS) < 1e-3f;
            (int iS, float ihpS, float idmgS) = InterceptUnfolded("Standard");
            (int iH, float ihpH, float idmgH) = InterceptUnfolded("Harsh");
            (int iB, float ihpB, float idmgB) = InterceptUnfolded("Builder");
            bool intercept = iS == 3 && iH == 3 && iB == 3 && ihpS > 0f && idmgS > 0f && Math.Abs(ihpS - hpS) < 1e-3f && Math.Abs(idmgS - dmgS) < 1e-3f
                             && Math.Abs(ihpH / ihpS - 1.2f) < 1e-3f && Math.Abs(idmgH / idmgS - 1.2f) < 1e-3f
                             && Math.Abs(ihpB / ihpS - 0.8f) < 1e-3f && Math.Abs(idmgB / idmgS - 0.8f) < 1e-3f;
            Expect(intercept, $"H2 行进中被拦截就地展开（SiegeService.Intercept，战斗内核读数）：标准 {iS} 台 耐久 {ihpS:F1} / 伤害 {idmgS:F2}（与到达展开相同）；严酷 {ihpH:F1} / {idmgH:F2}（×1.2）；建造者 {ihpB:F1} / {idmgB:F2}（×0.8）");
            Expect(ok, $"H 攻城展开（战斗内核读数）：同一支 3 台护甲机，标准 耐久 {hpS:F1} / 伤害 {dmgS:F2}；严酷 {hpH:F1} / {dmgH:F2}（×1.2）；建造者 {hpB:F1} / {dmgB:F2}（×0.8）；自定义按标准 ×1——与难度说明公开写出的倍率一致（FGR-FAC-003）");
        }

        // ── I 新游戏面板 ──────────────────────────────────────────────────────────

        private static void CheckNewGamePanel()
        {
            VisualElement root = F.MountUxml(NewGameUxml, out GameObject go);
            try
            {
                NewGamePanelUIToolkit panel = go.AddComponent<NewGamePanelUIToolkit>();
                panel.BindView(root);
                DifficultyChoice got = default;
                int started = 0;
                NewGamePanelUIToolkit.Open((seed, settings, diff) =>
                {
                    started++;
                    got = diff;
                }, () => { });
                DifficultyPickerView pk = panel.Difficulty;
                bool def = NewGamePanelUIToolkit.IsOpen && pk.PresetCount == 4 && pk.Choice.Id == "Standard" && pk.PresetButton("Standard").text.StartsWith("▸", StringComparison.Ordinal)
                           && pk.DescriptionText.Contains(GameText.Format("difficulty.line.enemy", "1", "1")) && !pk.CustomVisible;
                bool harsh = ClickButton(pk.PresetButton("Harsh")) && ClickButton(panel.StartButton) && started == 1 && got.Id == "Harsh" && !NewGamePanelUIToolkit.IsOpen;
                // 再开：回到默认标准；选自定义 + 拖滑条 → “开始”交回自定义三个倍率。
                NewGamePanelUIToolkit.Open((seed, settings, diff) =>
                {
                    started++;
                    got = diff;
                }, () => { });
                bool reset = pk.Choice.Id == "Standard";
                ClickButton(pk.PresetButton("Custom"));
                pk.FrequencySlider.value = 0.8f;
                pk.ScaleSlider.value = 1.5f;
                pk.WarningSlider.value = 1.26f;
                bool custom = ClickButton(panel.StartButton) && started == 2 && got.Id == "Custom" && Eq(got.Frequency, 0.8f) && Eq(got.Scale, 1.5f) && Eq(got.Warning, 1.3f);
                // 旧调用方（只关心种子与设置）照样能开始。
                int legacy = 0;
                NewGamePanelUIToolkit.Open((seed, settings) => legacy++, () => { });
                bool legacyOk = ClickButton(panel.StartButton) && legacy == 1;
                // 主菜单“开始”之后的真实建档入口（MainMenuUI.BuildNewCampaignState，CreateNewCampaign 调它）：开局难度与自定义倍率写进存档，不算修改。
                WorldSettings ws = WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId);
                CampaignState s = MainMenuUI.BuildNewCampaignState("fgdiff-ng", 6391, ws, got);
                RaidDifficultyState d = DifficultyService.StateOf(s);
                bool written = s.DifficultyId == "Custom" && d.StartDifficultyId == "Custom" && Eq(d.CustomFrequency, 0.8f) && Eq(d.CustomScale, 1.5f) && Eq(d.CustomWarning, 1.3f)
                               && d.ChangeCount == 0 && DifficultyService.KeptThroughout(s, "Custom") && Eq(DifficultyService.Scale(s), 1.5f);
                CampaignState h = MainMenuUI.BuildNewCampaignState("fgdiff-ng-h", 6392, ws, P("Harsh"));
                RaidDifficultyState dh = DifficultyService.StateOf(h);
                bool writtenHarsh = h.DifficultyId == "Harsh" && dh.StartDifficultyId == "Harsh" && dh.ChangeCount == 0 && !dh.EverLeftStart && DifficultyService.KeptThroughout(h, "Harsh")
                                    && Eq(DifficultyService.Scale(h), 1.3f);
                Expect(def && harsh && reset && custom && legacyOk && written && writtenHarsh,
                    $"I 新游戏面板（真 UXML、按钮真点击）：默认“▸ 标准”、说明写出敌人 ×1；选“严酷”点“开始”交回严酷；再开回到标准，选“自定义”拖滑条 0.8 / 1.5 / 1.26 → 交回 0.8 / 1.5 / 1.3（按步长）；" +
                    "只关心种子的旧调用方照常；主菜单真实建档入口 MainMenuUI.BuildNewCampaignState 把自定义 / 严酷开局写进新战役（DifficultyId 与开局难度一致，不算修改，“全程保持”成立）");
            }
            finally
            {
                NewGamePanelUIToolkit.Close();
                Object.DestroyImmediate(go);
            }
        }

        // ── J 布局探针与英文 ──────────────────────────────────────────────────────

        private static void CheckLayoutAndEnglish()
        {
            string probe = UiToolkitLayoutProbe.Probe(PanelUxml, "DifficultyWindow", stressFill: true, prepare: rr =>
            {
                rr.panel.visualTree.Q<VisualElement>("DifficultyRoot")?.RemoveFromClassList("uk-hidden");
                rr.panel.visualTree.Q<VisualElement>("DiffCustomBox")?.RemoveFromClassList("uk-hidden");
            });
            string probeNg = UiToolkitLayoutProbe.Probe(NewGameUxml, "NewGameWindow", stressFill: true, prepare: rr =>
            {
                rr.panel.visualTree.Q<VisualElement>("NewGameRoot")?.RemoveFromClassList("uk-hidden");
                rr.panel.visualTree.Q<VisualElement>("NewGameDiffCustomBox")?.RemoveFromClassList("uk-hidden");
            });
            string pauseUxml = File.ReadAllText(UiKitFolder + "PauseMenu.uxml");
            Expect(probe.Contains("PASS") && !probe.Contains("FAIL") && probeNg.Contains("PASS") && !probeNg.Contains("FAIL") && pauseUxml.Contains("PauseDifficulty"),
                "J1 难度面板与新游戏面板（含自定义滑条）布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]) + " / " +
                (probeNg.Contains("FAIL") ? probeNg : probeNg.Split('\n')[0]) + "；入口：暂停菜单“难度”");

            CampaignState s = NewWorld(6401, P("Harsh"));
            DifficultyService.Change(s, new DifficultyChoice("Custom", 1.1f, 1.2f, 1.3f), out _);
            VisualElement root = F.MountUxml(PanelUxml, out GameObject go);
            DifficultyPanelUIToolkit.InWorldOverrideForTests = true;
            GameSettings.SetLanguage(GameLanguage.En);
            try
            {
                DifficultyPanelUIToolkit panel = go.AddComponent<DifficultyPanelUIToolkit>();
                panel.BindView(root);
                DifficultyPanelUIToolkit.Open();
                string all = panel.CurrentText + panel.Picker.DescriptionText + string.Join("", Enumerable.Range(0, panel.HistoryRowCount).Select(panel.HistoryText))
                             + string.Join("", RaidCatalog.Difficulties.Select(r => panel.Picker.PresetButton(r.Id)?.text)) + panel.ApplyButton.text;
                Expect(!HasCjk(all) && !GameText.ContainsMarker(all) && all.Contains("Custom") && panel.HistoryRowCount == 2,
                    "J2 英文：难度面板的当前难度、按钮、说明、修改记录全部是英文（" + Short(panel.CurrentText + " | " + panel.HistoryText(1)) + "）");
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                DifficultyPanelUIToolkit.Close();
                DifficultyPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── K 性能 ────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(6411, new DifficultyChoice("Custom", 1.4f, 1.1f, 0.9f));
            PassGrace(s);
            for (int i = 0; i < 8; i++)
            {
                RaidDirectorService.RequestStoryRaid(s, i % 2 == 0 ? "silent" : "foundry", 1 + i % 4);
            }
            WorldSimulation.StepMany(2);
            const int n = 100000;
            float sink = 0f;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                sink += DifficultyService.Frequency(s) + DifficultyService.Scale(s) + DifficultyService.Warning(s) + DifficultyService.EnemyHealthMul(s) + (DifficultyService.StoryOnly(s) ? 1f : 0f);
            }
            double readUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency / n;
            long c0 = Stopwatch.GetTimestamp();
            DifficultyService.Change(s, P("Harsh"), out _);
            double changeMs = (Stopwatch.GetTimestamp() - c0) * 1000.0 / Stopwatch.Frequency;
            PerfLines.Add($"难度读取（5 个倍率一组）每组 {readUs:F3} μs（阈值 2 μs，导演 / 攻城每次用到时 O(1)）；中途修改一次（8 个计划重新编成）{changeMs:F3} ms（阈值 5 ms，只在玩家确认修改时发生）；校验和 {sink:F0}");
            PerfGate.Expect(Plans(s).Length == 8, $"K1 难度读取每组 {readUs:F3} μs（阈值 2 μs）",
                new[] { PerfGate.Le(readUs, 2.0, "难度读取 μs") }, Expect, Line);
            PerfGate.Expect(true, $"K2 中途修改一次（8 个计划重新编成）{changeMs:F3} ms（阈值 5 ms）",
                new[] { PerfGate.Le(changeMs, 5.0, "难度修改 ms") }, Expect, Line);
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
