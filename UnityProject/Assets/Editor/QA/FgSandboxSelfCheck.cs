using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
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
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG14-SBX-02 自定义沙盒（FG12 FGR-SBX-010、011、第 3～5 节；验收 FGT-SBX-004、005）自检：走主菜单同一建档入口（<c>MainMenuUI.BuildNewCampaignState</c>）起真实世界跑——
    /// A 入口与面板（主菜单“沙盒模式”、新游戏面板沙盒段 / 关闭保证 / 开局前成就说明、沙盒设置面板、研发树入口）；B 开局设置各项生效（种子与世界设置、起始资源、研发全部解锁、关闭保证、区域、敌方强度、突袭倍率、事件频率）；
    /// C 负向：关闭突袭时确实没有突袭（各种触发、静默夜照常断信号但没有进攻）；D 开局后修改（只有四项能改、立即生效、记进存档、关突袭取消未出发的计划）；
    /// E 没有剧情（叙事节点 / 演出 / 主线 / 揭示都不触发）与关键材料用研究点换取；F 成就：沙盒不计入剧情类成就；G 暂停与 0.5x～3x；H 存读档（含旧档兼容）；I 观察一致。
    /// </summary>
    public static class FgSandboxSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string NewGameUxml = "Assets/GameRes/Raw/UI/UiKit/NewGamePanel.uxml";
        private const string SandboxUxml = "Assets/GameRes/Raw/UI/UiKit/SandboxPanel.uxml";
        private const string ResearchUxml = "Assets/GameRes/Raw/UI/UiKit/ResearchTreePanel.uxml";
        private const string PauseUxml = "Assets/GameRes/Raw/UI/UiKit/PauseMenu.uxml";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("QA/自检/FG 自定义沙盒")]
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
            Line("\n[自定义沙盒] 主菜单沙盒模式、沙盒设置各项生效、关闭突袭没有突袭、开局后修改记进存档、没有剧情与关键材料换取、沙盒不计入剧情类成就（FG14-SBX-02）");
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
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsandbox-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                QuestCatalog.Reload();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                HeritageService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                SandboxService.ResetCountersForTests();
                DayNightCycle.Install();
                SilentNightService.Install(); // 正式入口（GameRoot 同一处）：静默夜 / 天气由世界步推进
                WeatherService.Install();

                Step(CheckEntryAndPanels);
                Step(CheckStartSettings);
                Step(CheckEffectiveAct);
                Step(CheckRaidsOff);
                Step(CheckRuntimeChanges);
                Step(CheckWeatherAndSilentNight);
                Step(CheckStoryOffAndExchange);
                Step(CheckAchievements);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckObservedEqualsUnobserved);
            }
            finally
            {
                NarrativeService.StopListeningForTests();
                SilentNightService.ResetForTests();
                DayNightCycle.ResetForTests();
                WeatherService.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
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
            Line($"  通过 {_pass} 项，失败 {_fail} 项");
            return _fail;
        }

        // ─────────────────────────────── A 入口与面板 ───────────────────────────────

        private static void CheckEntryAndPanels()
        {
            // 设置表 = FGR-SBX-011 的八项（选项数与顺序）；每个选项都有中文名，没有文本键标记。
            var counts = SandboxService.Defs.Select(d => $"{d.Id}:{d.Options}").ToArray();
            bool names = SandboxService.Defs.All(d => !GameText.ContainsMarker(GameText.Get(d.NameKey))
                                                    && Enumerable.Range(0, d.Options).All(o => !GameText.ContainsMarker(GameText.Get(d.OptionKey(o)))));
            Expect(string.Join(",", counts) == "raids:4,silent_night:2,weather:2,events:4,start_resources:2,research:2,regions:2,enemy:3" && names
                   && SandboxService.Defs.Where(d => d.Runtime).Select(d => d.Id).SequenceEqual(new[] { "raids", "silent_night", "weather", "events" })
                   && SandboxService.OptionName("raids", 0) == "关闭" && SandboxService.OptionName("regions", 1) == "全部开放",
                $"A1 沙盒设置项 = FGR-SBX-011（{string.Join(" ", counts)}），开局后能改的只有突袭 / 静默夜 / 天气 / 事件频率（FG12 第 3 节）");

            // 新游戏面板：剧情模式没有沙盒段、没有“关闭保证”；沙盒模式有八排设置、起始区第三个按钮、开局前的成就规则说明。
            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(NewGameUxml).CloneTree();
            var go = new GameObject("__FgSandboxNewGame") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                NewGamePanelUIToolkit panel = go.AddComponent<NewGamePanelUIToolkit>();
                panel.BindView(tree);
                NewGamePanelUIToolkit.Open((seed, settings, diff) => { }, () => { });
                bool story = !panel.IsSandboxMode && !panel.SandboxSectionVisible && panel.StartOffButton == null && panel.TitleText == GameText.Get("ui.newgame.title");
                NewGamePanelUIToolkit.Close();
                int started = 0;
                SandboxSetup got = null;
                bool gotOff = false;
                WorldSettings gotSettings = null;
                NewGamePanelUIToolkit.OpenSandbox((seed, settings, diff, sb, off) =>
                {
                    started++;
                    got = sb;
                    gotOff = off;
                    gotSettings = settings;
                }, () => { });
                string note = panel.SandboxNoteText;
                bool sandbox = panel.IsSandboxMode && panel.SandboxSectionVisible && panel.StartOffButton != null && panel.SandboxOption("raids", 0) != null
                               && panel.SandboxOption("enemy", 2) != null && panel.SandboxOption("raids", 2).text.StartsWith("▸", StringComparison.Ordinal)
                               && note.Contains("剧情类成就") && note.Contains("研究点") && note.Contains("突袭、静默夜、天气、事件频率") && !GameText.ContainsMarker(note + panel.StoryNoteText + panel.TitleText);
                bool clicks = ClickButton(panel.SandboxOption("raids", 0)) && ClickButton(panel.SandboxOption("weather", 1)) && ClickButton(panel.SandboxOption("regions", 1))
                              && ClickButton(panel.StartOffButton) && panel.StartOffButton.text.StartsWith("▸", StringComparison.Ordinal)
                              && !panel.LevelButton(4, 0).text.StartsWith("▸", StringComparison.Ordinal)
                              && ClickButton(panel.LevelButton(3, 2)) && ClickButton(panel.StartButton);
                bool passed = started == 1 && got != null && got.Get("raids") == 0 && got.Get("weather") == 1 && got.Get("regions") == 1 && got.Get("silent_night") == 0
                              && gotOff && gotSettings != null && gotSettings.Levels[3] == 2 && !NewGamePanelUIToolkit.IsOpen;
                // 选回“标准”取消关闭保证。
                NewGamePanelUIToolkit.OpenSandbox((seed, settings, diff, sb, off) => gotOff = off, () => { });
                ClickButton(panel.StartOffButton);
                ClickButton(panel.LevelButton(4, 0));
                ClickButton(panel.StartButton);
                Expect(story && sandbox && clicks && passed && !gotOff,
                    $"A2 新游戏面板：剧情模式没有沙盒段与“关闭保证”（{story}）；沙盒模式八项设置 + 起始区第三个按钮 + 开局前说明成就规则（{sandbox}：“{note}”）；点选后“开始”交回沙盒设置、关闭保证与领地距离（{clicks}/{passed}）；选回标准取消关闭保证（{!gotOff}）");
            }
            finally
            {
                NewGamePanelUIToolkit.Close();
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }

            // 研发树“关键材料换取”入口、暂停菜单“沙盒设置”入口都在正式 UXML 里（默认隐藏，沙盒档才显示）。
            VisualElement rt = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(ResearchUxml).CloneTree();
            VisualElement pm = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PauseUxml).CloneTree();
            Button rb = rt.Q<Button>("ResearchSandboxExchange");
            Button pb = pm.Q<Button>("PauseSandbox");
            Expect(rb != null && rb.ClassListContains("uk-hidden") && pb != null && pb.ClassListContains("uk-hidden"),
                "A3 研发树有“关键材料换取…”、暂停菜单有“沙盒设置”（默认隐藏，沙盒档显示）");

            // 布局探针（四种分辨率 + 超长文字 + USS 体检）：沙盒设置面板、新游戏面板的沙盒段。
            string p1 = UiToolkitLayoutProbe.Probe(SandboxUxml, "SandboxWindow", stressFill: true, prepare: rr =>
            {
                rr.panel.visualTree.Q<VisualElement>("SandboxRoot")?.RemoveFromClassList("uk-hidden");
            });
            string p2 = UiToolkitLayoutProbe.Probe(NewGameUxml, "NewGameWindow", stressFill: true, prepare: rr =>
            {
                rr.panel.visualTree.Q<VisualElement>("NewGameRoot")?.RemoveFromClassList("uk-hidden");
                rr.panel.visualTree.Q<VisualElement>("NewGameSandbox")?.RemoveFromClassList("uk-hidden");
            });
            // 修复（审查 P2）：沙盒档暂停菜单多出“沙盒设置”按钮之后的布局。
            string p3 = UiToolkitLayoutProbe.Probe(PauseUxml, "PauseMenuWindow", stressFill: true, prepare: rr =>
            {
                var pgo = new GameObject("__FgSandboxPauseProbe") { hideFlags = HideFlags.HideAndDontSave };
                pgo.AddComponent<PauseMenuUIToolkit>().BindView(rr.panel.visualTree);
                rr.panel.visualTree.Q<Button>("PauseSandbox")?.RemoveFromClassList("uk-hidden");
                Object.DestroyImmediate(pgo);
            });
            Expect(p1.Contains("PASS") && !p1.Contains("FAIL") && p2.Contains("PASS") && !p2.Contains("FAIL") && p3.Contains("PASS") && !p3.Contains("FAIL"),
                "A4 布局探针：沙盒设置面板 " + (p1.Contains("FAIL") ? p1 : p1.Split('\n')[0]) + "；新游戏面板沙盒段 " + (p2.Contains("FAIL") ? p2 : p2.Split('\n')[0])
                + "；沙盒档暂停菜单 " + (p3.Contains("FAIL") ? p3 : p3.Split('\n')[0]));
        }

        // ─────────────────────────────── B 开局设置生效 ───────────────────────────────

        private static void CheckStartSettings()
        {
            const string worldId = "R2O0P1D2S0";
            CampaignState std = NewWorld(41001, null, false, observe: false);
            int stdScrap = std.Scrap;
            int stdTech = std.TechData;
            int stdDone = std.Research.CompletedNodes.Length;
            string stdShare = WorldGenService.ShareCode(std);
            bool stdReport = WorldGenService.PlanFor(std)?.StartReport != null;
            float stdHp = DifficultyService.EnemyHealthMul(std);

            CampaignState s = NewWorld(41001, SandboxSetup.Defaults().Set("start_resources", 1).Set("research", 1).Set("regions", 1).Set("enemy", 2).Set("raids", 3).Set("events", 0),
                true, observe: false, worldId: worldId);
            int ready = ResearchCatalog.Nodes.Count(n => n.IsReady);
            bool research = s.Research.CompletedNodes.Length == ready && stdDone < ready && ResearchCatalog.Nodes.Where(n => n.IsReady).All(n => ResearchService.IsCompleted(s, n.Id));
            bool rich = s.Scrap == stdScrap + SandboxService.RichExtraScrap && s.TechData == stdTech + SandboxService.RichExtraTechData && SandboxService.RichExtraScrap > 0;
            bool world = s.World.WorldSeed == 41001 && s.World.WorldSettingsId == worldId && s.World.NoStartGuarantee && WorldGenService.PlanFor(s)?.StartReport == null && stdReport
                         && WorldGenService.SettingsFor(s).TerritoryDistanceScale > 1f;
            CampaignState plainSandbox = NewWorld(41001, SandboxSetup.Defaults(), false, observe: false);
            bool shareSame = WorldGenService.ShareCode(plainSandbox) == stdShare && plainSandbox.World.WorldSettingsId == std.World.WorldSettingsId
                             && WorldGenService.PlanFor(plainSandbox)?.StartReport != null;
            Expect(research && rich && world && shareSame && s.Progress.IsSandbox && !std.Progress.IsSandbox,
                $"B1 开局：研发全部解锁 = {s.Research.CompletedNodes.Length}/{ready} 个已就绪节点（剧情档 {stdDone}）（{research}）；起始资源丰富 废料 {s.Scrap}（标准 {stdScrap}）技术数据 {s.TechData}（{rich}）；"
                + $"种子与世界设置（领地远）照用 FG17 生成参数、起始区关闭保证 = 没有保证报告（{world}）；不关保证时短码与剧情档相同（{shareSame}）");

            // 区域全部开放：第二幕起的阵营领地开局就显示、酸沼防腐涂层视为已有；按进度解锁的沙盒开局看不到。
            PlannedTerritory late = WorldGenService.PlanFor(s)?.Territories.FirstOrDefault(t => t.IsFaction && t.Act >= 2);
            CampaignState byProgress = NewWorld(41002, SandboxSetup.Defaults(), false, observe: false);
            PlannedTerritory late2 = WorldGenService.PlanFor(byProgress)?.Territories.FirstOrDefault(t => t.IsFaction && t.Act >= 2);
            bool regions = late != null && HomeExplorationService.TerritoryVisible(s, late) && AcidMarshService.HasCoating(s) && SandboxService.TerritoryAct(s) == 3
                           && late2 != null && !HomeExplorationService.TerritoryVisible(byProgress, late2) && !AcidMarshService.HasCoating(byProgress);
            Expect(regions, $"B2 区域：全部开放 → 第 {late?.Act} 幕领地开局就显示、酸沼防腐涂层已有；按进度解锁 → 看不到、没有涂层（{regions}）");

            // 敌方强度、突袭倍率、事件频率。
            var row = EnemyDefs.Rows.FirstOrDefault(r => r.MaxHp > 0);
            float hpRatio = row == null ? 0f : EnemyDefs.SeedHealth(s, row) / EnemyDefs.SeedHealth(std, row);
            bool enemy = Math.Abs(DifficultyService.EnemyHealthMul(s) / stdHp - 1.4f) < 1e-4f && Math.Abs(hpRatio - 1.4f) < 1e-3f
                         && Math.Abs(DifficultyService.EnemyDamageMul(s) / DifficultyService.EnemyDamageMul(std) - 1.4f) < 1e-4f;
            bool raidMul = Math.Abs(DifficultyService.RaidScale(s) / DifficultyService.RaidScale(std) - 1.5f) < 1e-4f
                           && Math.Abs(DifficultyService.RaidFrequency(s) / DifficultyService.RaidFrequency(std) - 1.5f) < 1e-4f
                           && DifficultyService.Scale(s) == DifficultyService.Scale(std);
            bool events = EventDirectorService.Frequency(s) == EventDirectorService.FrequencyOff && EventDirectorService.Rate(s) == 0 && EventDirectorService.Frequency(std) == EventDirectorService.FrequencyStandard;
            // 真实计划的预算：同一剧情突袭在“高”档是“标准”档的 1.5 倍。
            CampaignState sbStd = NewWorld(41003, SandboxSetup.Defaults(), false, observe: false);
            int bStd = BudgetOfStoryRaid(sbStd);
            CampaignState sbHigh = NewWorld(41003, SandboxSetup.Defaults().Set("raids", 3), false, observe: false);
            int bHigh = BudgetOfStoryRaid(sbHigh);
            CampaignState sbLow = NewWorld(41003, SandboxSetup.Defaults().Set("raids", 1), false, observe: false);
            int bLow = BudgetOfStoryRaid(sbLow);
            bool budget = bStd > 0 && Math.Abs(bHigh - bStd * 1.5) <= 1.5 && Math.Abs(bLow - bStd * 0.5) <= 1.5;
            Expect(enemy && raidMul && events && budget,
                $"B3 敌方强度高 = 耐久 / 伤害 ×1.4（播种耐久 ×{hpRatio:0.###}）（{enemy}）；突袭 高 / 低 = 频率与规模 ×1.5 / ×0.5、不改难度本身（{raidMul}），同一突袭预算 低 {bLow} / 标准 {bStd} / 高 {bHigh}（{budget}）；事件频率 关闭 = 随机事件倍率 0（{events}）");
        }

        /// <summary>
        /// 修复（审查 P1）：沙盒没有剧情推幕，按幕门控的非剧情内容读 <see cref="SandboxService.EffectiveAct"/>：剧情档 = 当前幕；
        /// 区域“全部开放”= 3；“按进度解锁”= 揭开过危害的阵营领地里最大的幕。逐个消费点验：突袭阵营资格、世界事件幕门槛、天气幕行、商队货品与来访。
        /// </summary>
        private static void CheckEffectiveAct()
        {
            CampaignState story = NewWorld(41005, null, false, observe: false);
            CampaignState open = NewWorld(41005, SandboxSetup.Defaults().Set("regions", 1), false, observe: false);
            CampaignState prog = NewWorld(41005, SandboxSetup.Defaults(), false, observe: false);
            int storyAct1 = SandboxService.EffectiveAct(story);
            story.Progress.Act = 3;
            bool basis = storyAct1 == 1 && SandboxService.EffectiveAct(story) == 3 && SandboxService.EffectiveAct(open) == 3 && SandboxService.EffectiveAct(prog) == 1
                         && open.Progress.Act == 1 && prog.Progress.Act == 1;

            // 突袭：给一个第二幕起、有突袭编成的阵营记暴露贡献 → 全部开放的沙盒选它出兵；按进度解锁（未探索到）不选它。
            PlannedTerritory late = WorldGenService.PlanFor(open)?.Territories
                .Where(t => t.IsFaction && t.Act >= 2 && RaidCatalog.HasUnits(t.Id)).OrderBy(t => t.Act).FirstOrDefault();
            string raidOpen = null, raidProg = null, raidProgSeen = null;
            int progSeenAct = 0;
            if (late != null)
            {
                foreach (CampaignState c in new[] { open, prog })
                {
                    c.SignalExposureTotals = new[] { new SignalExposureTotalRecord { Kind = "fgsandbox", Faction = late.Id, Added = 50f } };
                }
                raidOpen = RaidDirectorService.MostStimulatedFaction(open);
                raidProg = RaidDirectorService.MostStimulatedFaction(prog);
                // 按进度解锁：亲自揭开这片领地的危害后，这一幕就开放（领地显示、突袭阵营同一口径）。
                var h = HomeExplorationService.HazardForTerritory(late.Id);
                if (h != null)
                {
                    prog.Grid.Exploration.HazardsSeen = prog.Grid.Exploration.HazardsSeen.Concat(new[] { h.Id }).ToArray();
                }
                progSeenAct = SandboxService.EffectiveAct(prog);
                raidProgSeen = RaidDirectorService.MostStimulatedFaction(prog);
            }
            bool raids = late != null && raidOpen == late.Id && raidProg != late.Id && progSeenAct == late.Act && raidProgSeen == late.Id
                         && HomeExplorationService.TerritoryVisible(prog, late);

            // 世界事件：ActMin ≥ 2 的事件（轨道坠落 / 信号幽灵）在全部开放的沙盒过幕门槛，未探索的按进度沙盒不过。
            var actOk = typeof(EventDirectorService).GetMethod("ActOk", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            CampaignState prog2 = NewWorld(41006, SandboxSetup.Defaults(), false, observe: false);
            bool events = actOk != null;
            string eventLine = string.Empty;
            foreach (string id in new[] { "orbital_drop", "signal_ghost" })
            {
                EventDirectorDef def = EventDirectorService.Def(id);
                bool o = def != null && (bool)actOk.Invoke(null, new object[] { open, def });
                bool p = def != null && (bool)actOk.Invoke(null, new object[] { prog2, def });
                events &= def != null && def.ActMin >= 2 && o && !p;
                eventLine += $"{id}(幕≥{def?.ActMin}) 全开 {o} / 未探索 {p}；";
            }

            // 天气：全部开放的沙盒用第三幕那一行，与剧情档第三幕一致；按进度沙盒与剧情档第一幕一致。
            WeatherService.ActRow(open, out float bo, out double cmo, out _, out _, out _, out _);
            WeatherService.ActRow(story, out float b3, out double cm3, out _, out _, out _, out _);
            CampaignState story1 = NewWorld(41007, null, false, observe: false);
            WeatherService.ActRow(prog2, out float bp, out double cmp, out _, out _, out _, out _);
            WeatherService.ActRow(story1, out float b1, out double cm1, out _, out _, out _, out _);
            bool weather = bo == b3 && cmo == cm3 && bp == b1 && cmp == cm1;

            // 商队：第一幕结束才来访；第二幕起的货品按有效幕上架。
            int lateWares = CaravanService.Rows.Count(w => w.ActMin >= 2 && CaravanService.Available(open, w));
            int lateWaresProg = CaravanService.Rows.Count(w => w.ActMin >= 2 && CaravanService.Available(prog2, w));
            bool caravan = CaravanService.Act1Ended(open) && !CaravanService.Act1Ended(prog2) && lateWares > 0 && lateWaresProg == 0;

            Expect(basis && raids && events && weather && caravan,
                $"B4 沙盒有效幕：剧情档 = 当前幕、全部开放 = 3、按进度 = 1（{basis}）；突袭：第 {late?.Act} 幕阵营 {late?.Id} 全开选中 {raidOpen} / 未探索 {raidProg} / 揭开危害后有效幕 {progSeenAct} 选中 {raidProgSeen}（{raids}）；"
                + $"事件 {eventLine}（{events}）；天气幕行 全开 = 剧情第三幕、按进度 = 剧情第一幕（{weather}）；商队 全开来访且第二幕起货品 {lateWares} 件可上架、未探索 {lateWaresProg} 件（{caravan}）");
        }

        private static int BudgetOfStoryRaid(CampaignState s)
        {
            RaidDirectorService.RequestStoryRaid(s, RaidCatalog.FactionChemical, 2, "fgsandbox");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            RaidPlanRecord p = RaidDirectorService.StateOf(s)?.Plans.FirstOrDefault(x => x != null && x.StoryId == "fgsandbox");
            return p?.Budget ?? -1;
        }

        // ─────────────────────────────── C 关闭突袭 ───────────────────────────────

        private static void CheckRaidsOff()
        {
            // 对照：标准档的沙盒，剧情报复触发会建出计划。
            CampaignState ctl = NewWorld(41010, SandboxSetup.Defaults(), false, observe: false);
            RaidDirectorService.RequestStoryRaid(ctl, RaidCatalog.FactionChemical, 1, "fgsandbox");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            int ctlPlans = LivePlans(ctl);

            CampaignState s = NewWorld(41011, SandboxSetup.Defaults().Set("raids", 0), false, observe: false);
            int sup0 = SandboxService.SuppressedRaidTriggers;
            RaidDirectorService.RequestStoryRaid(s, RaidCatalog.FactionChemical, 2, "fgsandbox");
            RaidDirectorService.OnPurificationTowerTargeted(s);
            RaidDirectorService.OnMegastructureStageCompleted(s, 3);
            s.SignalExposure = 100f;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            int suppressed = SandboxService.SuppressedRaidTriggers - sup0;
            int plans1 = LivePlans(s);
            // 静默夜照常来、照样断信号，但没有进攻（FG12 第 4 节）。
            SilentNightState n = SilentNightService.StateOf(s);
            GameClock.SkipForTests(s, n.StartTick - 2 - GameClock.Ticks);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool night = SilentNightService.IsActive(s);
            int plans2 = LivePlans(s);
            // 再走 20 个游戏日（暴露 100、骚扰、周期 4 级都会排的条件下）仍然一波都没有。
            for (int d = 0; d < 20; d++)
            {
                GameClock.SkipForTests(s, RaidDirectorService.DayTicks(1) - GameClock.StepHz);
                WorldSimulation.StepMany(GameClock.StepHz);
            }
            int plans3 = LivePlans(s);
            suppressed = SandboxService.SuppressedRaidTriggers - sup0;
            int raidsTotal = RaidDirectorService.StateOf(s).RaidCount;
            Expect(ctlPlans == 1 && suppressed >= 4 && plans1 == 0 && night && plans2 == 0 && plans3 == 0 && raidsTotal == 0,
                $"C1 关闭突袭时确实没有突袭：对照（标准档）建出 {ctlPlans} 个计划；关闭档丢掉 {suppressed} 次触发（剧情报复 / 净化塔 / 巨构 / 静默夜 / 暴露），计划 {plans1}；静默夜照常开始（{night}）没有进攻（{plans2}）；再过 20 日 {plans3} 个计划");
        }

        private static int LivePlans(CampaignState s) =>
            RaidDirectorService.StateOf(s)?.Plans.Count(p => p != null && p.State <= RaidDirectorService.StateDeparted) ?? 0;

        // ─────────────────────────────── D 开局后修改 ───────────────────────────────

        private static void CheckRuntimeChanges()
        {
            CampaignState s = NewWorld(41020, SandboxSetup.Defaults(), false, observe: false);
            RaidDirectorService.RequestStoryRaid(s, RaidCatalog.FactionChemical, 1, "fgsandbox");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            RaidPlanRecord p = RaidDirectorService.StateOf(s).Plans.FirstOrDefault(x => x != null && x.StoryId == "fgsandbox");
            long t = GameClock.Ticks;
            SandboxService.ChangeResult r1 = SandboxService.TryChange(s, "raids", 0);
            bool cancelled = p != null && p.State == RaidDirectorService.StateCancelled && LivePlans(s) == 0;
            SandboxChangeRecord c = SandboxService.Changes(s).LastOrDefault();
            bool rec = c != null && c.Setting == "raids" && c.From == 2 && c.To == 0 && c.Tick == t && s.Progress.Sandbox.ChangeCount == 1;
            SandboxService.ChangeResult r2 = SandboxService.TryChange(s, "raids", 0);
            SandboxService.ChangeResult r3 = SandboxService.TryChange(s, "enemy", 2);
            SandboxService.ChangeResult r4 = SandboxService.TryChange(s, "start_resources", 1);
            SandboxService.ChangeResult r5 = SandboxService.TryChange(s, "raids", 9);
            SandboxService.ChangeResult r6 = SandboxService.TryChange(s, "events", EventDirectorService.FrequencyHigh);
            bool events = EventDirectorService.Frequency(s) == EventDirectorService.FrequencyHigh && SandboxService.Changes(s).Count == 2;
            SandboxService.ChangeResult r7 = SandboxService.TryChange(s, "raids", 2);
            RaidDirectorService.RequestStoryRaid(s, RaidCatalog.FactionChemical, 1, "fgsandbox2");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool backOn = LivePlans(s) == 1;
            CampaignState story = NewWorld(41021, null, false, observe: false);
            SandboxService.ChangeResult r8 = SandboxService.TryChange(story, "raids", 0);
            CampaignSession.Set(Slot, s); // 面板读当前存档（世界已卸载，面板不依赖地点）
            Expect(r1 == SandboxService.ChangeResult.Changed && cancelled && rec && r2 == SandboxService.ChangeResult.Unchanged && r3 == SandboxService.ChangeResult.Locked
                   && r4 == SandboxService.ChangeResult.Locked && r5 == SandboxService.ChangeResult.Invalid && r6 == SandboxService.ChangeResult.Changed && events
                   && r7 == SandboxService.ChangeResult.Changed && backOn && r8 == SandboxService.ChangeResult.NotSandbox && !story.Progress.IsSandbox,
                $"D1 开局后修改：突袭改成关闭 → 未出发的计划取消（{cancelled}）、记一条修改记录（{rec}）；同值不记（{r2}）、敌方强度 / 起始资源开局后不能改（{r3}/{r4}）、越界拒绝（{r5}）；"
                + $"事件频率改“多”写进事件导演（{events}）；改回标准后突袭重新会来（{backOn}）；剧情档不能改（{r8}）");

            // 面板：四排按钮、开局时定下的项只读、修改记录（最近的在前）、点按钮生效。
            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(SandboxUxml).CloneTree();
            var go = new GameObject("__FgSandboxPanel") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                SandboxPanelUIToolkit.InWorldOverrideForTests = true;
                SandboxPanelUIToolkit panel = go.AddComponent<SandboxPanelUIToolkit>();
                panel.BindView(tree);
                SandboxPanelUIToolkit.Open();
                bool open = SandboxPanelUIToolkit.IsOpen && panel.RuntimeRowCount == 4 && panel.OptionButton("raids", 2).text.StartsWith("▸", StringComparison.Ordinal)
                            && panel.OptionButton("enemy", 0) == null && panel.FixedText.Contains("敌方强度") && panel.FixedText.Contains("不能再改")
                            && panel.ChangeRowCount == 3 && panel.ChangeText(0).Contains("突袭") && panel.ExchangeRowCount == QuestCatalog.KeyMaterials.Count
                            && panel.StatusText.StartsWith("沙盒", StringComparison.Ordinal) && !GameText.ContainsMarker(panel.FixedText + panel.ChangeText(0) + panel.ExchangeText(0) + panel.StatusText);
                bool click = ClickButton(panel.OptionButton("weather", 1)) && SandboxService.Get(s, "weather") == 1 && panel.ChangeRowCount == 4
                             && panel.ChangeText(0).Contains("天气") && panel.FeedbackText.Contains("记进存档");
                panel.SetOpen(false);
                CampaignSession.Set(Slot, story);
                SandboxPanelUIToolkit.Open();
                bool storyClosed = !SandboxPanelUIToolkit.IsOpen;
                Expect(open && click && storyClosed,
                    $"D2 沙盒设置面板：四排可改项（当前档“▸”）、开局时定下的项只读、修改记录 {panel.ChangeRowCount} 行最近的在前、关键材料 {panel.ExchangeRowCount} 行（{open}）；点“天气 关”立即生效并记录（{click}）；剧情档打不开（{storyClosed}）");
            }
            finally
            {
                SandboxPanelUIToolkit.InWorldOverrideForTests = false;
                SandboxPanelUIToolkit.Close();
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
        }

        // ─────────────────────────────── 天气与静默夜 ───────────────────────────────

        private static void CheckWeatherAndSilentNight()
        {
            // 天气：开着的沙盒等到第一段天气开始 → 改成关 → 立即结束、排好的全部撤掉；之后 20 日都没有天气；改回开重新排。
            CampaignState s = NewWorld(41030, SandboxSetup.Defaults(), false, observe: false);
            WeatherFrontState w = WeatherService.StateOf(s);
            for (int i = 0; i < 40 && w.Fronts.Length == 0; i++)
            {
                GameClock.SkipForTests(s, RaidDirectorService.DayTicks(1));
                WorldSimulation.StepMany(GameClock.StepHz);
            }
            bool started = false;
            if (w.Fronts.Length > 0)
            {
                GameClock.SkipForTests(s, w.Fronts[0].StartTick - 2 - GameClock.Ticks);
                WorldSimulation.StepMany(GameClock.StepHz);
                started = WeatherService.ActiveKind(s) != WeatherService.Clear;
            }
            SandboxService.TryChange(s, "weather", 1);
            WorldSimulation.StepMany(2);
            bool ended = WeatherService.ActiveKind(s) == WeatherService.Clear && w.Fronts.Length == 0;
            bool never = true;
            for (int d = 0; d < 20; d++)
            {
                GameClock.SkipForTests(s, RaidDirectorService.DayTicks(1) - GameClock.StepHz);
                WorldSimulation.StepMany(GameClock.StepHz);
                never &= WeatherService.ActiveKind(s) == WeatherService.Clear && w.Fronts.Length == 0;
            }
            SandboxService.TryChange(s, "weather", 0);
            WorldSimulation.StepMany(GameClock.StepHz);
            bool replanned = w.PlannedUntilTick > GameClock.Ticks;
            Expect(started && ended && never && replanned,
                $"E0a 天气：开着时照常来（{started}）；改成关 → 进行中的立即结束、排好的撤掉（{ended}）；之后 20 日没有天气（{never}）；改回开重新排（{replanned}）");

            // 静默夜：开局关 → 到了原定那一夜不预警、不开始；开着的进行中改成关 → 立即结束（断信号恢复）。
            CampaignState off = NewWorld(41031, SandboxSetup.Defaults().Set("silent_night", 1), false, observe: false);
            SilentNightState n = SilentNightService.StateOf(off);
            long firstStart = n.StartTick;
            GameClock.SkipForTests(off, firstStart - 2 - GameClock.Ticks);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool none = !SilentNightService.IsActive(off) && n.Count == 0 && n.StartTick > firstStart;
            CampaignState on = NewWorld(41032, SandboxSetup.Defaults(), false, observe: false);
            SilentNightState m = SilentNightService.StateOf(on);
            GameClock.SkipForTests(on, m.StartTick - 2 - GameClock.Ticks);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool active = SilentNightService.IsActive(on);
            int serial = m.NextDebrisSerial;
            int ends = SilentNightService.EndCount;
            SandboxService.TryChange(on, "silent_night", 1);
            WorldSimulation.StepMany(2);
            bool stopped = !SilentNightService.IsActive(on);
            // 修复（审查 P2）：提前关掉不算熬过一夜，不刷风暴残骸（否则开着等它开始再关就能白拿奖励）。
            bool noDebris = m.NextDebrisSerial == serial && m.LastDebrisIds.Length == 0 && SilentNightService.EndCount == ends + 1;
            Expect(none && active && stopped && noDebris,
                $"E0b 静默夜：关 → 原定第 {firstStart} 步那一夜没来（{none}）；开着时照常来（{active}），进行中改成关立即结束（{stopped}）、不刷风暴残骸（{noDebris}）");
        }

        // ─────────────────────────────── E 没有剧情 / 关键材料换取 ───────────────────────────────

        private static void CheckStoryOffAndExchange()
        {
            CampaignState story = NewWorld(41040, null, false, observe: false);
            bool storyWake = NarrativeService.Trigger(story, NarrativeCatalog.TriggerNewGame) && story.Progress.PendingCutscene == NarrativeService.WakeId;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            int storyMain = MainQuests(story);

            CampaignState s = NewWorld(41041, SandboxSetup.Defaults(), false, observe: false);
            int blocked0 = NarrativeService.BlockedInSandbox;
            NarrativeService.OnCampaignEntered(s);
            int delivered = 0;
            int tried = 0;
            foreach (NarrativeEntryDef def in NarrativeCatalog.All)
            {
                if (def == null || string.IsNullOrEmpty(def.Trigger))
                {
                    continue;
                }
                tried++;
                if (NarrativeService.Trigger(s, def.Trigger))
                {
                    delivered++;
                }
            }
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            int main = MainQuests(s);
            bool noStory = delivered == 0 && s.Progress.Narrative.Length == 0 && string.IsNullOrEmpty(s.Progress.PendingCutscene) && s.Progress.Act == 1
                           && NarrativeService.BlockedInSandbox - blocked0 >= tried && tried > 0 && main == 0;
            bool reveal = !RevealService.CanConfirm(s, out string why) && why == RevealService.DenySandbox && !RevealService.Confirm(s, out _)
                          && !GameText.ContainsMarker(RevealService.DenyText(why));
            Expect(storyWake && storyMain > 0 && noStory && reveal,
                $"E1 没有剧情：对照剧情档开场演出待播（{storyWake}）、主线 {storyMain} 个；沙盒逐个触发全部 {tried} 个剧情节点 → 0 个投放、没有演出、不推幕、主线 {main} 个（{noStory}）；"
                + $"建完巨构也不点亮信标 / 不进终局（{reveal}：“{RevealService.DenyText(why)}”）");

            // 关键材料用研究点换（物品进核心保管库、蓝图记账本）；剧情档不能换。
            GameConfig.fg.KeyMaterial core = QuestCatalog.KeyMaterialOf("listening_array_core");
            GameConfig.fg.KeyMaterial bp = QuestCatalog.KeyMaterialOf(AcidMarshService.CoatingBlueprintId);
            int cost = SandboxService.ExchangeCost(core);
            s.Research.Points = 0;
            bool poor = !SandboxService.TryExchange(s, core.Id, out string r0) && r0 == "sandbox.exchange.deny.points";
            s.Research.Points = 500;
            bool got = SandboxService.TryExchange(s, core.Id, out _) && HomeInventory.Stock(s, core.Id) == 1 && s.Research.Points == 500 - cost && cost == SandboxService.ExchangePointsPerAct * core.Act;
            bool owned = !SandboxService.TryExchange(s, core.Id, out string r1) && r1 == "sandbox.exchange.deny.owned";
            ResearchCatalog.TryGet("signal.override_t1", out ResearchNodeDef t1);
            bool gate = t1 != null && ResearchService.KeysPresent(s, t1) && CampaignObjectiveTracker.HasKeyMaterial(s, core);
            int before = s.Research.Points;
            bool blueprint = SandboxService.TryExchange(s, bp.Id, out _) && CampaignEventLedger.Contains(s, CampaignObjectiveTracker.BlueprintEventId(bp.Id))
                             && AcidMarshService.HasCoating(s) && s.Research.Points == before - SandboxService.ExchangeCost(bp)
                             && !SandboxService.TryExchange(s, bp.Id, out string r2) && r2 == "sandbox.exchange.deny.owned";
            HomeInventory.RemoveUpTo(s, ItemCatalog.Find(core.Id), 1); // 投进建筑后保管库里为 0 → 可以再换
            bool again = SandboxService.ExchangeBlock(s, core) == null;
            story.Research.Points = 500;
            bool storyDenied = !SandboxService.TryExchange(story, core.Id, out string r3) && r3 == "sandbox.exchange.deny.story";
            bool all = QuestCatalog.KeyMaterials.All(k => SandboxService.ExchangeCost(k) > 0 && !GameText.ContainsMarker(GameText.Get(k.NameKey)));
            Expect(poor && got && owned && gate && blueprint && again && storyDenied && all && s.Progress.Sandbox.ExchangeCount == 2,
                $"E2 关键材料用研究点换：不够不换（{poor}）；{cost} 点换到监听阵列核进核心保管库（{got}），已有不重复换（{owned}），超控阵列 T1 的关键材料门槛满足（{gate}）；"
                + $"防腐涂层蓝图记账本、涂层生效（{blueprint}）；投进建筑后可以再换（{again}）；剧情档不能换（{storyDenied}）；{QuestCatalog.KeyMaterials.Count} 件都有价格（{all}）");
        }

        private static int MainQuests(CampaignState s) =>
            QuestCatalog.Rows.Count(q => q.Category == QuestCatalog.CatMain && CampaignObjectiveTracker.FindQuest(s, q.Id) != null);

        // ─────────────────────────────── F 成就 ───────────────────────────────

        private static void CheckAchievements()
        {
            CampaignState story = NewWorld(41050, null, false, observe: false);
            CampaignState s = NewWorld(41051, SandboxSetup.Defaults(), false, observe: false);
            NarrativeService.OnCampaignEntered(s);
            // 剧情类成就的判据（结局账本、揭示账本、幕）在沙盒里都到不了：信标点不亮、叙事不推幕。
            RevealService.Confirm(s, out _);
            bool noStoryFacts = !RevealService.Revealed(s) && !CampaignEventLedger.Contains(s, EndingService.EndingEventId) && s.Progress.Act == 1;
            CampaignSlotMetadata meta = SaveAndMeta(s);
            string card = CampaignSlotText.FgFields(meta);
            Expect(!SandboxService.StoryAchievementsAllowed(s) && SandboxService.StoryAchievementsAllowed(story) && noStoryFacts && meta.IsSandbox && card.Contains("沙盒"),
                $"F1 FGT-SBX-005：沙盒存档不计入剧情类成就（判定口 StoryAchievementsAllowed：沙盒 false / 剧情档 true），结局 / 揭示 / 幕这些剧情成就判据在沙盒里都不会出现（{noStoryFacts}）；存档卡标“沙盒”：“{card}”");
        }

        // ─────────────────────────────── G 暂停与倍速 ───────────────────────────────

        private static void CheckPauseSpeed()
        {
            var rows = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewWorld(41060, SandboxSetup.Defaults().Set("raids", 0), false, observe: true);
                SilentNightState n = SilentNightService.StateOf(s);
                long target = n.StartTick;
                GameClock.SkipForTests(s, target - 2 * GameClock.StepHz - GameClock.Ticks);
                long t0 = GameClock.Ticks;
                GameClock.SetPaused(true);
                SandboxService.ChangeResult r = SandboxService.TryChange(s, "weather", 1); // 暂停里也能改，记录的是暂停那一步
                for (int i = 0; i < 60; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool paused = GameClock.Ticks == t0 && !SilentNightService.IsActive(s) && r == SandboxService.ChangeResult.Changed && SandboxService.Changes(s)[0].Tick == t0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                for (int i = 0; i < 20000 && GameClock.Ticks <= target + GameClock.StepHz; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                GameClock.SetSpeed(1f);
                bool exact = SilentNightService.IsActive(s) && LivePlans(s) == 0 && RaidDirectorService.StateOf(s).RaidCount == 0;
                ok &= paused && exact;
                rows.Add($"{speed}x 暂停不走且可改设置（{paused}）、静默夜第 {target} 步照常开始且没有突袭（{exact}）");
            }
            Expect(ok, "G1 暂停与 0.5x～3x：" + string.Join("；", rows));
        }

        // ─────────────────────────────── H 存读档 ───────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(41070, SandboxSetup.Defaults().Set("enemy", 0).Set("regions", 1).Set("start_resources", 1), true, observe: true);
            SandboxService.TryChange(s, "raids", 1);
            SandboxService.TryChange(s, "events", EventDirectorService.FrequencyLow);
            SandboxService.TryChange(s, "silent_night", 1);
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string sig0 = Sig(s);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(Slot);
            string card = CampaignSlotText.FgFields(meta);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            string sigA = Sig(s);
            long t1 = GameClock.Ticks;
            CampaignState l = RestoreSlot(out string fail);
            if (l == null)
            {
                Fail("H1 读档失败：" + fail);
                return;
            }
            string sigL = Sig(l);
            WorldSimulation.StepMany((int)(t1 - GameClock.Ticks));
            string sigB = Sig(l);
            bool cardOk = meta.IsSandbox && meta.SandboxCode == SandboxService.Code(s) && card.Contains("沙盒") && card.Contains("突袭 低") && card.Contains("关闭保证")
                          && !GameText.ContainsMarker(card);
            Expect(r.Success && sigL == sig0 && sigA == sigB && cardOk && WorldGenService.PlanFor(l)?.StartReport == null,
                $"H1 存读档：沙盒标记、设置、修改记录、事件频率、关闭保证读回 = 存档时（{sigL == sig0}），再走 20 秒与不读档一致（{sigA == sigB}）；存档卡“{card}”（{cardOk}）"
                + (sigL != sig0 ? $"\n存档时：{sig0}\n读回：{sigL}" : string.Empty) + (sigA != sigB ? $"\n不读档：{sigA}\n读档后：{sigB}" : string.Empty));

            // 旧档（没有沙盒域）读入 = 全部标准档、没有记录；剧情档的存档卡不显示沙盒。
            var old = CampaignState.CreateNew("fgsandbox-old", "Standard", 1);
            old.Progress.Sandbox = null;
            CampaignFgStateDomains.EnsureAll(old);
            bool compat = old.Progress.Sandbox != null && old.Progress.Sandbox.Raids == 2 && old.Progress.Sandbox.Changes.Length == 0 && !old.World.NoStartGuarantee
                          && SandboxService.Code(old) == string.Empty && SandboxService.RaidMul(old) == 1f && SandboxService.EnemyMul(old) == 1f && SandboxService.SilentNightOn(old) && SandboxService.WeatherOn(old);
            Expect(compat, $"H2 旧档没有沙盒域 → 全部标准档、剧情档不受沙盒判定影响（{compat}）");
        }

        private static string Sig(CampaignState s)
        {
            SandboxState d = s.Progress.Sandbox;
            var sb = new StringBuilder();
            sb.Append(s.Progress.IsSandbox).Append('|').Append(SandboxService.Code(s)).Append('|').Append(d.ChangeCount).Append('|').Append(d.ExchangeCount).Append('|')
                .Append(EventDirectorService.Frequency(s)).Append('|').Append(s.World.NoStartGuarantee).Append('|').Append(s.World.WorldSettingsId).Append('|').Append(s.Scrap).Append('|')
                .Append(SilentNightService.IsActive(s)).Append('|').Append(SilentNightService.StateOf(s).StartTick).Append('|').Append(WeatherService.ActiveKind(s)).Append('|')
                .Append(WeatherService.StateOf(s).Fronts.Length).Append('|').Append(LivePlans(s)).Append('|').Append(s.Progress.Narrative.Length);
            foreach (SandboxChangeRecord c in d.Changes)
            {
                sb.Append(';').Append(c.Tick).Append(',').Append(c.Day).Append(',').Append(c.Setting).Append(',').Append(c.From).Append(',').Append(c.To);
            }
            return sb.ToString();
        }

        // ─────────────────────────────── I 观察一致 ───────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string Run(bool observe)
            {
                CampaignState s = NewWorld(41080, SandboxSetup.Defaults().Set("raids", 0).Set("weather", 1), false, observe);
                SilentNightState n = SilentNightService.StateOf(s);
                GameClock.SkipForTests(s, n.StartTick - 5 * GameClock.StepHz - GameClock.Ticks);
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                SandboxService.TryChange(s, "weather", 0);
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                return Sig(s);
            }
            string a = Run(true);
            string b = Run(false);
            Expect(a == b, $"I1 观察 / 不观察时沙盒设置的效果与修改记录一致（关突袭穿过静默夜、中途开天气）" + (a != b ? $"\n观察：{a}\n不观察：{b}" : string.Empty));
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        /// <summary>走主菜单同一建档入口（剧情档 <paramref name="sandbox"/> = null）；领地距离等世界设置照用 FG17 生成参数。</summary>
        private static CampaignState NewWorld(int seed, SandboxSetup sandbox, bool noGuarantee, bool observe, string worldId = null)
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
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            WorldSettings settings = WorldSettings.Resolve(WorldGenVersions.Current, worldId ?? WorldGenContent.DefaultPresetId);
            CampaignState s = MainMenuUI.BuildNewCampaignState("fgsandbox-" + seed + (sandbox != null ? "-sb" : string.Empty), seed, settings,
                DifficultyService.Preset(DifficultyService.Standard), sandbox, noGuarantee);
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            WorldSimulation.StepMany(2);
            return s;
        }

        private static CampaignSlotMetadata SaveAndMeta(CampaignState s)
        {
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            return CampaignSaveService.GetSlotMetadata(Slot);
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

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgSandboxSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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

        private static void Line(string text) => _report.AppendLine(text);
    }
}
