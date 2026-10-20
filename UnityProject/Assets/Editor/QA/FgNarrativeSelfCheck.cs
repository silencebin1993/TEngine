using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
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
using GameLogic.Narrative;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG9-NAR-01 叙事投放框架与资料库的自动验收（FG11 FGR-NAR-001～004、FGR-NAR-010 前五个节点、第 8 / 9 / 10 节；FG13 FGU-38、FGR-UX-051 / 060；
    /// 卡片验收 FGT-END-001 的第一幕部分、负向“演出中退出游戏”）。
    /// 全部起真实系统：主菜单同一个新建入口（<see cref="MainMenuUI.BuildNewCampaignState"/>）、真实统一时钟与世界模拟、真实远征出发 + 正式到达事务、
    /// 真实领地首领判定（<see cref="TerritoryControlService.MarkLeaderDefeated"/>）、真实旧终端完工、真实监听情报产出、真实自动存档与恢复编排（真文件）、
    /// 真 UXML 面板（演出播放器 / 资料库 / 暂停菜单）、真实确认框与 Esc 栈、真实通知中心。
    /// D1 数据；J1 新游戏开场演出（自动存档 → 接管画面 → 逐字字幕 → 跳过确认取消 → 看完收录）；N1 演出中退出游戏（读档回到演出前、重新播放、只收一次）；
    /// J2 第一幕五个剧情节点按触发逐个出现（FGT-END-001 第一幕）；J3 其余投放（旧终端 / 监听截获 / 核心备忘）；N2 负向矩阵（新存档看不到别的存档、幂等、坏触发、
    /// 待播条目不在表里、播放中战役被换掉、旧档缺字段）；T1 暂停与 0.5x～3x；S1 真文件存读档；U1 界面；P1 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgNarrativeSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgNarrativeSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgNarrativeSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 6;
        private const int SlotB = 5;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static GameObject _cutGo;
        private static NarrativeCutscenePlayerUIToolkit _player;

        [MenuItem("BinGames/QA/自检/FG 叙事投放框架与资料库")]
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
            Line("\n[叙事投放框架与资料库] 四种投放、资料库（按幕 / 来源筛选、未读、剧情回顾、按存档）、演出可跳过可重看、演出中退出回到演出前、文本速度与字幕大小、第一幕五个节点（FG9-NAR-01）");
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
            string originalCodex = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgnarrative-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                UiTuningValues.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                Campaign.Content.FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                DayNightCycle.ReloadTuning();
                HomeExplorationService.Reload();
                NarrativeCatalog.Reload();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex_mechanics.json");
                MechanicCodex.ResetForTests();
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                NarrativeService.ResetForTests();
                NarrativeService.EnsureListening();
                _cutGo = null;
                VisualElement croot = F.MountUxml(UiKitFolder + "NarrativeCutscene.uxml", out _cutGo);
                _player = _cutGo.AddComponent<NarrativeCutscenePlayerUIToolkit>();
                _player.BindView(croot);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "叙事框架在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），只在触发 / 面板刷新 / 演出播放时运行，与实体数无关；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckOpening);
                Step(CheckQuitDuringCutscene);
                Step(CheckActOneNodes);
                Step(CheckOtherChannels);
                Step(CheckNegatives);
                Step(CheckTimingMatrix);
                Step(CheckSaveLoad);
                Step(CheckUi);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"叙事自检抛异常：{e}");
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
                NarrativeService.AbortPlaying();
                NarrativeService.StopListeningForTests();
                NarrativeService.ResetForTests();
                NarrativeService.CutsceneSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.CutsceneEnter, toast: false);
                NarrativeLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                NarrativeCutscenePlayerUIToolkit.InWorldOverrideForTests = false;
                if (_cutGo != null)
                {
                    Object.DestroyImmediate(_cutGo);
                }
                _player = null;
                WorldPlanetView.Shutdown();
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
                MechanicCodex.FilePathOverrideForTests = originalCodex;
                MechanicCodex.ResetForTests();
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
            Line($"  · [叙事投放框架与资料库] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Hz => Math.Max(1, GameClock.StepHz);

        /// <summary>主菜单“开始”同一条建档入口 + 载入家园；不走一步（统一时钟 0 = 新游戏），再按正式入口进入战役。</summary>
        private static CampaignState NewGame(int seed, int slot = Slot, bool enter = true)
        {
            NarrativeService.AbortPlaying();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            HomeExplorationService.ResetForTests();
            IntelService.ResetSessionState();
            InputRouter.Reset();
            UiEscapeStack.Clear();
            UiConfirmDialog.DiscardAll();
            NotificationCenter.ResetForTests();
            CampaignState s = MainMenuUI.BuildNewCampaignState("fgnarrative-" + seed, seed,
                WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId), DifficultyService.Preset("Standard"));
            CampaignSaveService.Save(slot, s, SaveReason.NewCampaign);
            CampaignSession.Set(slot, s);
            NotificationCenter.Bind(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            if (enter)
            {
                NarrativeService.OnCampaignEntered(s);
            }
            return s;
        }

        /// <summary>播放器按真实帧推进，直到当前演出结束（或超时）。返回是否结束。</summary>
        private static bool PlayToEnd(float dt = 0.1f, int maxFrames = 2000)
        {
            for (int i = 0; i < maxFrames && NarrativeCutscenePlayerUIToolkit.IsShowing; i++)
            {
                _player.Tick(dt);
            }
            return !NarrativeCutscenePlayerUIToolkit.IsShowing;
        }

        private static int CountOf(string typeId) => NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Sum(e => e.Count);

        private static string LastDetail(string typeId) =>
            NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Select(e => e.Latest?.DetailText ?? string.Empty).LastOrDefault() ?? string.Empty;

        private static string Ids(CampaignState s) => string.Join(",", (s?.Progress?.Narrative ?? Array.Empty<NarrativeEntryRecord>()).Select(r => r.Id));

        private static int Spawn(CampaignState s, string chassis, string blueprint, Vector2 offset)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, core + offset, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
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

        // ── D1 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_narrative as p;print('\\n'.join(p.dump_lines()))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("NE\t", StringComparison.Ordinal) || l.StartsWith("NL\t", StringComparison.Ordinal)).ToList();
            var rt = t.TbNarrativeEntry.DataList.Select(e => string.Join("\t", "NE", e.Id, e.Source, e.Act, e.Node, e.Trigger, e.TitleKey, e.BodyKey, e.SetAct, e.SortOrder))
                .Concat(t.TbNarrativeLine.DataList.Select(l => string.Join("\t", "NL", l.Id, l.EntryId, l.Order, l.TextKey,
                    l.HoldSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))).ToList();
            bool table = code == 0 && src.Count == rt.Count && src.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(rt.OrderBy(x => x, StringComparer.Ordinal));
            IReadOnlyList<NarrativeEntryDef> all = NarrativeCatalog.All;
            bool sources = NarrativeCatalog.Sources.All(src0 => all.Any(d => d.Source == src0));
            string[] nodes = { "node.wake", "node.sky_eyes", "node.guns_up", "node.first_fragment", "node.what_waits" };
            string[] triggers = { "new_game", "site:" + FracturedCityLayout.RegionId, "site:" + FoundryOutpostLayout.RegionId, "leader:silent", "leader:foundry" };
            bool nodeRows = nodes.Select((id, i) => NarrativeCatalog.TryGet(id, out NarrativeEntryDef d) && d.Node && d.Act == 1 && d.Trigger == triggers[i]).All(x => x)
                            && NarrativeCatalog.Find("node.wake").IsCutscene && NarrativeCatalog.Find("node.wake").Lines.Count >= 2
                            && NarrativeCatalog.Find("node.what_waits").SetAct == 2;
            // 单一真相：派生条目的文本键就是源数据的键（数据核心资料 / 舰队片段 / 归还里程碑 / 领地收复 / 旧终端）。
            bool derived = AnalysisCatalog.Lore.All(l => NarrativeCatalog.TryGetByTrigger("analysis:" + l.Id, out NarrativeEntryDef d) && d.Source == NarrativeCatalog.SourceTerminal)
                           && IntelCatalog.Fragments.All(fr => NarrativeCatalog.TryGetByTrigger("intel:" + fr.Id, out NarrativeEntryDef d) && d.BodyKey == fr.TextKey && d.Source == NarrativeCatalog.SourceIntercept)
                           && t.TbReturnMilestone.DataList.All(m => NarrativeCatalog.TryGetByTrigger("memo:" + m.Id, out NarrativeEntryDef d) && d.TitleKey == m.MemoTitleKey && d.BodyKey == m.MemoBodyKey)
                           && t.TbTerritoryControl.DataList.All(c => NarrativeCatalog.TryGetByTrigger("memo:" + TerritoryControlService.MemoPrefix + c.Id, out NarrativeEntryDef d) && d.BodyKey == c.MemoBodyKey)
                           && t.TbTerritoryTemplate.DataList.SelectMany(tt => tt.TerminalLoreKeys.Split(',')).Select(k => k.Trim()).Where(k => k.Length > 0)
                               .All(k => NarrativeCatalog.TryGetByTrigger("terminal:" + k, out NarrativeEntryDef d) && d.BodyKey == k);
            // 审查修复（P1）：家园遗迹点的背景资料（fg.TbRelicRing.loreKeys，FGR-NAR-001“遗迹里的文字日志”）同样有资料库条目（数据终端来源）。
            var relicKeys = t.TbRelicRing.DataList.SelectMany(rr => rr.LoreKeys.Split(',')).Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
            bool relicDerived = relicKeys.Count > 0 && relicKeys.All(k => NarrativeCatalog.TryGetByTrigger("terminal:" + k, out NarrativeEntryDef d) && d.BodyKey == k && d.Source == NarrativeCatalog.SourceTerminal);
            derived &= relicDerived;
            var keys = new List<string>();
            foreach (NarrativeEntryDef d in all)
            {
                keys.Add(d.TitleKey);
                keys.Add(d.BodyKey);
                keys.AddRange(d.Lines.Select(l => l.TextKey));
            }
            keys.AddRange(new[]
            {
                "narrative.source.memo", "narrative.source.terminal", "narrative.source.intercept", "narrative.source.cutscene", "narrative.source.all", "narrative.act.all", "narrative.act.n",
                "narrative.intercept.noise", "narrative.notify.detail", "narrative.library.title", "narrative.library.tab.entries", "narrative.library.tab.recap", "narrative.library.empty",
                "narrative.library.empty_filter", "narrative.library.empty_recap", "narrative.library.source_tag", "narrative.library.detail_hint", "narrative.library.meta", "narrative.library.recap_row",
                "narrative.library.replay", "narrative.library.mark_all", "narrative.library.footer", "narrative.cutscene.skip", "narrative.cutscene.next", "narrative.cutscene.hint",
                "narrative.cutscene.skip_title", "narrative.cutscene.skip_line", "narrative.cutscene.skip_confirm", "narrative.cutscene.skip_cancel", "pause.library", "pause.library_unread",
                "pause.text_title", "pause.text_speed", "pause.text_speed_instant", "pause.text_speed_tip", "pause.subtitle_scale", "pause.subtitle_scale_tip", "pause.text_reset",
                "codex.narrative.title", "codex.narrative.body", "codex.narrative.hint",
            });
            var missing = keys.Distinct().Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string z) || string.IsNullOrWhiteSpace(z) || !GameText.TryGet(k, GameLanguage.En, out string e) || string.IsNullOrWhiteSpace(e)).ToList();
            bool tuning = GameSettings.NarrativeTextSpeedMin > 0f && GameSettings.NarrativeTextSpeedMin <= GameSettings.NarrativeTextSpeedDefault && GameSettings.NarrativeTextSpeedDefault <= GameSettings.NarrativeTextSpeedMax
                          && GameSettings.SubtitleScaleMin > 0f && GameSettings.SubtitleScaleMin <= 1f && GameSettings.SubtitleScaleMax >= 1.5f
                          && UiTuningValues.Get("narrative.subtitle_font_px") > 0f && UiTuningValues.Get("narrative.library_font_px") > 0f;
            NotifyType nt = t.TbNotifyType.GetOrDefault(NarrativeService.NotifyType);
            bool notify = nt != null && nt.AwaySection == "event";
            bool hooks = new[] { GuidanceHooks.NarrativeFirstEntry, GuidanceHooks.NarrativeLibraryFirstOpen, GuidanceHooks.NarrativeFirstCutscene, GuidanceHooks.NarrativeFirstSkip, GuidanceHooks.NarrativeFirstReplay }
                .All(h => GuidanceHooks.Known.Contains(h));
            bool codex = t.TbCodexEntry.GetOrDefault("codex.narrative") != null;
            bool problems = NarrativeCatalog.Problems.Count == 0;
            Expect(table && sources && nodeRows && derived && missing.Count == 0 && tuning && notify && hooks && codex && problems,
                $"D1 数据：fg.TbNarrativeEntry / TbNarrativeLine 与源数据逐行一致（源 {src.Count} 行 / 表 {rt.Count} 行）{table}；四种投放都有内容 {sources}；FGR-NAR-010 第一幕五个节点（触发 / 幕 / 演出字幕 / 第一幕结束推进到第二幕）{nodeRows}；" +
                $"数据核心 / 舰队片段 / 归还里程碑 / 领地收复 / 旧终端 / 遗迹点背景资料（{relicKeys.Count} 条，{relicDerived}）的条目引用源数据的键（单一真相）{derived}；中英文本齐全（缺 {string.Join("、", missing)}）；文本速度与字幕大小范围 {tuning}；" +
                $"通知类型进离家报告“事件”段 {notify}；引导钩子 {hooks}；图鉴条目 {codex}；运行时载入无问题 {problems}（{string.Join("；", NarrativeCatalog.Problems)}）");
        }

        // ── J1 新游戏开场演出 ─────────────────────────────────────────────────────

        private static void CheckOpening()
        {
            GameSettings.ResetNarrativeText();
            int saves0 = NarrativeService.CutsceneSaveCount;
            CampaignState s = NewGame(9101);
            bool pending = s.Progress.PendingCutscene == NarrativeService.WakeId && NarrativeService.Count(s) == 0 && (s.Clock?.Ticks ?? -1) == 0;
            bool runningBefore = !GameRoot.IsWorldPaused;
            // 播放器的下一帧：世界已载入、没有模态 → 自动存档 → 接管画面。
            _player.Tick(0.016f);
            bool showing = NarrativeCutscenePlayerUIToolkit.IsShowing && _player.PanelVisible && InputRouter.IsModalOwner(_player) && UiEscapeStack.Contains(_player);
            bool paused = GameRoot.IsWorldPaused;
            LoadResult onDisk = CampaignSaveService.Load(Slot);
            bool saved = NarrativeService.CutsceneSaveCount == saves0 + 1 && NarrativeService.LastCutsceneSave.Success && onDisk.Success
                         && onDisk.State.Progress.PendingCutscene == NarrativeService.WakeId && onDisk.State.LastSaveReason == SaveReason.CutsceneEnter && NarrativeService.Count(onDisk.State) == 0;
            // 逐字：默认每秒 40 字 → 0.1 秒 4 个字（真实时间，与世界暂停无关）。
            string full = _player.Cutscene.LineText;
            _player.Tick(0.1f);
            string partial = _player.LineText;
            bool typing = full.Length > 4 && partial.Length == Math.Min(full.Length, (int)Math.Floor(0.1f * GameSettings.NarrativeTextSpeed + 1e-4f)) && full.StartsWith(partial, StringComparison.Ordinal)
                          && _player.TitleText == GameText.Get("narrative.node.wake.title") && _player.ProgressText == $"1 / {NarrativeCatalog.Find("node.wake").Lines.Count}"
                          && !_player.ReplayTagVisible;
            // 点一下补全，再点一下下一行。
            Click(_player.NextButton);
            bool completed = _player.LineText == full;
            Click(_player.NextButton);
            bool nextLine = _player.Cutscene.LineIndex == 1;
            // 跳过（首次观看）→ 确认框；“继续观看”= 取消 → 仍在播放、不收录。
            Click(_player.SkipButton);
            bool confirm = UiConfirmDialog.IsOpen && UiConfirmDialog.Current?.Title == GameText.Get("narrative.cutscene.skip_title")
                           && UiConfirmDialog.Current.Lines.Contains(GameText.Get("narrative.cutscene.skip_line")) && _player.Confirming;
            UiConfirmDialog.Cancel();
            bool kept = NarrativeCutscenePlayerUIToolkit.IsShowing && !_player.Confirming && NarrativeService.Count(s) == 0 && s.Progress.PendingCutscene == NarrativeService.WakeId;
            // Esc：同样先确认；再取消。
            bool escTop = UiEscapeStack.Top == _player;
            string topName = UiEscapeStack.Top?.GetType().Name ?? "null";
            UiEscapeStack.CloseTop();
            _player.Tick(0.016f);
            bool escConfirm = UiConfirmDialog.IsOpen && _player.Confirming;
            UiConfirmDialog.Cancel();
            bool escBack = UiEscapeStack.Contains(_player) && NarrativeCutscenePlayerUIToolkit.IsShowing;
            int notes0 = CountOf(NarrativeService.NotifyType);
            bool ended = PlayToEnd();
            NarrativeEntryRecord rec = NarrativeService.Find(s, NarrativeService.WakeId);
            bool filed = ended && rec != null && rec.Read && rec.Act == 1 && s.Progress.PendingCutscene.Length == 0 && !GameRoot.IsWorldPaused
                         && !InputRouter.IsModalOwner(_player) && !UiEscapeStack.Contains(_player) && CountOf(NarrativeService.NotifyType) == notes0 + 1
                         && LastDetail(NarrativeService.NotifyType).Contains(GameText.Get("narrative.node.wake.title"))
                         && GameSettings.HasSeenGuidanceHook(GuidanceHooks.NarrativeFirstCutscene) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.NarrativeFirstEntry);
            // 看完之后不会再播（同一存档只播一次）。
            _player.Tick(0.016f);
            bool once = !NarrativeCutscenePlayerUIToolkit.IsShowing && NarrativeService.Count(s) == 1;
            Expect(pending && runningBefore && showing && paused && saved,
                $"J1 新游戏：主菜单建档入口 → 进入战役记下待播开场演出“苏醒”（统一时钟 0）{pending}；播放器下一帧自动存档（存档里记着待播、原因 CutsceneEnter）{saved}后接管画面（模态 + Esc 层）{showing}、世界暂停 {paused}");
            Expect(typing && completed && nextLine,
                $"J1 逐字字幕：0.1 秒显示“{partial}”（每秒 {GameSettings.NarrativeTextSpeed} 字）{typing}；点“继续”先补全整行 {completed}、再点进下一行 {nextLine}");
            Expect(confirm && kept && escTop && escConfirm && escBack,
                $"J1 跳过（首次观看）：按钮与 Esc 都先弹确认（“跳过后可以随时在资料库里重看”）{confirm}/{escConfirm}；“继续观看”后仍在播放、没有收录 {kept}/{escBack}（Esc 栈顶 {topName} {escTop}）");
            Expect(filed && once,
                $"J1 看完：收进资料库（已读、第一幕）、清掉待播、世界恢复运行、释放输入、发“资料库新增：苏醒”通知、引导钩子 {filed}；之后不再播放 {once}");
        }

        // ── N1 演出中退出游戏 ─────────────────────────────────────────────────────

        private static void CheckQuitDuringCutscene()
        {
            CampaignState s = NewGame(9111);
            _player.Tick(0.016f);
            _player.Tick(1.5f);
            bool playing = NarrativeCutscenePlayerUIToolkit.IsShowing && GameRoot.IsWorldPaused;
            // 退出游戏：世界卸载、会话清空（没有任何存档写入）→ 播放器下一帧中止，不收录。
            WorldSimulation.UnloadAll();
            CampaignSession.Clear();
            GameClock.ResetSession();
            _player.Tick(0.016f);
            bool aborted = !NarrativeCutscenePlayerUIToolkit.IsShowing && NarrativeService.Playing == null && NarrativeService.Count(s) == 0;
            // 重新启动 → 读档：读回的是演出前的自动存档（待播仍在、资料库空）→ 进入战役 → 演出从第一行重新播放。
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("N1 读档失败：" + rr.Message);
                return;
            }
            CampaignState l = rr.State;
            bool before = l.Progress.PendingCutscene == NarrativeService.WakeId && NarrativeService.Count(l) == 0 && l.LastSaveReason == SaveReason.CutsceneEnter;
            CampaignSession.Set(Slot, l);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            NarrativeService.OnCampaignEntered(l);
            _player.Tick(0.016f);
            bool replays = NarrativeCutscenePlayerUIToolkit.IsShowing && _player.Cutscene.LineIndex == 0 && !_player.Cutscene.Replay && NarrativeService.SkipNeedsConfirm;
            // 这次确认跳过：只收一次。
            Click(_player.SkipButton);
            UiConfirmDialog.Confirm();
            bool skipped = !NarrativeCutscenePlayerUIToolkit.IsShowing && NarrativeService.Count(l) == 1 && NarrativeService.Has(l, NarrativeService.WakeId)
                           && l.Progress.PendingCutscene.Length == 0 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.NarrativeFirstSkip) && !GameRoot.IsWorldPaused;
            NarrativeService.OnCampaignEntered(l);
            _player.Tick(0.016f);
            bool noAgain = !NarrativeCutscenePlayerUIToolkit.IsShowing && NarrativeService.Count(l) == 1;
            Expect(playing && aborted && before && replays && skipped && noAgain,
                $"N1 负向“演出中退出游戏”（FG11 第 9 节）：播放到一半退出 → 中止不收录 {aborted}；读档回到演出前的自动存档（待播仍在、资料库空、原因 CutsceneEnter）{before}；进入后演出从第一行重新播放（仍算首次观看）{replays}；" +
                $"确认跳过后收录一次、世界恢复 {skipped}；再进入不再播放 {noAgain}");
        }

        // ── J2 第一幕五个剧情节点（FGT-END-001 第一幕）──────────────────────────────

        private static void CheckActOneNodes()
        {
            CampaignState s = NewGame(9121);
            _player.Tick(0.016f);
            PlayToEnd();
            WorldSimulation.StepMany(3);
            var order = new List<string>();
            bool Absent(CampaignState st, params string[] ids) => ids.All(id => !NarrativeService.Has(st, id));

            // 节点 2：第一次远征破碎都市（真实出发 + 正式到达事务）→ 数据终端“天上的眼睛”。
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(6f, -6f));
            WorldSimulation.StepMany(3);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            bool before2 = Absent(s, "node.sky_eyes", "node.guns_up", "node.first_fragment", "node.what_waits");
            int notes0 = CountOf(NarrativeService.NotifyType);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            s = CampaignSession.Current;
            bool notYet = !NarrativeService.Has(s, "node.sky_eyes");
            bool arrived = dep.Success && ExpeditionMarchService.ArriveNowForTests(s);
            s = CampaignSession.Current;
            bool n2 = arrived && notYet && NarrativeService.Has(s, "node.sky_eyes") && Absent(s, "node.guns_up", "node.first_fragment", "node.what_waits")
                      && CountOf(NarrativeService.NotifyType) == notes0 + 1 && LastDetail(NarrativeService.NotifyType).Contains(GameText.Get("narrative.source.terminal"));
            // 审查修复（P1）：到达后的那份自动存档（地点 Enter 写的，已在地点）读回来就带着这个节点——中途关游戏读档不会缺节点。
            LoadResult arrDisk = CampaignSaveService.Load(Slot);
            bool arrSaved = arrDisk.Success && NarrativeService.Has(arrDisk.State, "node.sky_eyes")
                            && ExpeditionMarchService.Current(arrDisk.State)?.Phase == ExpeditionMarchService.PhaseAtSite && ExpeditionMarchService.Current(arrDisk.State).VisitedSite;
            order.Add("sky_eyes");
            // 撤离回家（正式撤离入口 + 快进到家），之后再去别的地点。
            ExpeditionReturnService.ReturnResult ret = ExpeditionReturnService.TryConfirmEvacuation();
            bool back = ret.Success && ExpeditionMarchService.ReturnNowForTests(CampaignSession.Current);
            s = CampaignSession.Current;
            int countAfter2 = NarrativeService.Count(s);

            // 节点 3：第一次进入铸造前哨 → 核心备忘“朝天的炮口”（自己发“资料库新增”通知，不是“核心备忘”通知）。
            RegionRecord foundry = FoundryOutpostRegion.Find(s);
            if (foundry != null && foundry.State == RegionState.Locked)
            {
                foundry.State = RegionState.Available;
            }
            int[] team = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && (m.LogicId == a || m.LogicId == b)).Select(m => m.LogicId).ToArray();
            int memo0 = CountOf(NarrativeService.MemoNotifyType);
            ExpeditionDepartureService.DepartureResult dep2 = ExpeditionDepartureService.TryDepart(team, true, ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost);
            s = CampaignSession.Current;
            bool arrived2 = dep2.Success && ExpeditionMarchService.ArriveNowForTests(s);
            s = CampaignSession.Current;
            bool n3 = back && arrived2 && countAfter2 == 2 && NarrativeService.Has(s, "node.guns_up") && Absent(s, "node.first_fragment", "node.what_waits")
                      && CountOf(NarrativeService.MemoNotifyType) == memo0 && LastDetail(NarrativeService.NotifyType).Contains(GameText.Get("narrative.source.memo"));
            order.Add("guns_up");
            if (ExpeditionReturnService.TryConfirmEvacuation().Success)
            {
                ExpeditionMarchService.ReturnNowForTests(CampaignSession.Current);
            }
            s = CampaignSession.Current;
            // 第二次到达破碎都市：不再投放“天上的眼睛”。
            int beforeAgain = NarrativeService.Count(s);
            int[] team2 = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && (m.LogicId == a || m.LogicId == b)).Select(m => m.LogicId).ToArray();
            bool again = ExpeditionDepartureService.TryDepart(team2, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins).Success && ExpeditionMarchService.ArriveNowForTests(CampaignSession.Current);
            s = CampaignSession.Current;
            bool noRepeat = again && NarrativeService.Count(s) == beforeAgain;
            if (ExpeditionReturnService.TryConfirmEvacuation().Success)
            {
                ExpeditionMarchService.ReturnNowForTests(CampaignSession.Current);
            }
            s = CampaignSession.Current;

            // 节点 4：击败寂听主脑（首领 Story 的正式入口）→ 监听截获“第一个片段”；节点 5：击败铸造主核心 → 核心备忘“它在等什么”，第一幕结束。
            int act0 = s.Progress.Act;
            bool silent = TerritoryControlService.MarkLeaderDefeated(s, "silent");
            bool n4 = silent && NarrativeService.Has(s, "node.first_fragment") && !NarrativeService.Has(s, "node.what_waits") && s.Progress.Act == 1;
            int actEvents = 0;
            Action<int> onAct = _ => actEvents++;
            GameEvent.AddEventListener(NarrativeService.ActChangedEvent, onAct);
            bool foundryDown;
            try
            {
                foundryDown = TerritoryControlService.MarkLeaderDefeated(s, "foundry");
            }
            finally
            {
                GameEvent.RemoveEventListener(NarrativeService.ActChangedEvent, onAct);
            }
            bool n5 = foundryDown && NarrativeService.Has(s, "node.what_waits") && act0 == 1 && s.Progress.Act == 2 && actEvents == 1;
            var recap = new List<NarrativeEntryRecord>();
            NarrativeService.Recap(s, recap);
            string recapIds = string.Join(",", recap.Select(r => r.Id));
            bool recapOk = recapIds == "node.wake,node.sky_eyes,node.guns_up,node.first_fragment,node.what_waits" && recap.All(r => r.Act == 1)
                           && recap.Zip(recap.Skip(1), (x, y) => x.Serial < y.Serial && x.Tick <= y.Tick).All(v => v);
            Expect(arrSaved, $"J2 节点 2 存档：到达破碎都市后磁盘上的自动存档（已在地点、到过该地点）读回仍有“天上的眼睛”——投放在地点载入的自动存档之前 {arrSaved}（读档 {arrDisk.Outcome}）");
            Expect(before2 && n2, $"J2 节点 2 “天上的眼睛”：出发时还没有，远征队第一次到达破碎都市（正式到达事务）才收进资料库（数据终端），发一条“资料库新增”通知（{LastDetail(NarrativeService.NotifyType)}）{n2}" +
                (n2 ? string.Empty : $"（出发 {dep.Outcome} {string.Join("，", dep.Reasons ?? Array.Empty<string>())}；到达 {arrived}；出发时未收 {notYet}；资料库 {Ids(s)}）"));
            Expect(n3 && noRepeat, (n3 && noRepeat ? string.Empty : $"（出发 {dep2.Outcome} {string.Join("，", dep2.Reasons ?? Array.Empty<string>())}；到达 {arrived2}；返回 {back}；资料库 {Ids(s)}）") + $"J2 节点 3 “朝天的炮口”：第一次进入铸造前哨收进核心备忘，通知是“资料库新增”而不是重复的“核心备忘” {n3}；撤离回家后再去破碎都市不重复投放 {noRepeat}");
            Expect(n4 && n5, $"J2 节点 4 / 5：击败寂听主脑 → 监听截获“第一个片段”（仍在第一幕）{n4}；击败铸造主核心 → 核心备忘“它在等什么”，战役推进到第二幕并广播一次 {n5}");
            Expect(recapOk, $"J2 FGT-END-001（第一幕）：剧情回顾按发生先后列出五个节点（{recapIds}），都归第一幕 {recapOk}");
        }

        // ── J3 其余投放渠道 ───────────────────────────────────────────────────────

        private static void CheckOtherChannels()
        {
            CampaignState s = NewGame(9131);
            _player.Tick(0.016f);
            PlayToEnd();
            WorldSimulation.StepMany(2);
            // 旧终端（真实完工入口）：读到的记录（同一终端永远同一条）收进资料库“数据终端”，幕按领地。
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            var feats = new List<WorldFeature>();
            WorldFeature term = null;
            int termAct = 0;
            foreach (string tid in new[] { "silent", "foundry" })
            {
                PlannedTerritory pt = ctx.Plan.Find(tid);
                if (pt == null || term != null)
                {
                    continue;
                }
                feats.Clear();
                WorldFeatures.Query(ctx, pt.CenterX - pt.Radius, pt.CenterY - pt.Radius, pt.CenterX + pt.Radius, pt.CenterY + pt.Radius, feats);
                term = feats.FirstOrDefault(f => f.Kind == WorldFeatureKind.Terminal);
                termAct = 1;
            }
            bool termOk = false;
            string termEntry = null;
            if (term != null)
            {
                HomeGridService.RevealArea(s, new Vector2(term.X, term.Y), 6f);
                HomeExplorationService.TryGetRelic(s, term.Id, out HomeExplorationService.RelicInfo ti);
                string key = HomeExplorationService.LoreKeyFor(ti);
                int n0 = CountOf(NarrativeService.NotifyType);
                int tech = HomeExplorationService.CompleteRecovery(s, new WorkOrderRecord { TargetId = term.Id });
                NarrativeCatalog.TryGetByTrigger("terminal:" + key, out NarrativeEntryDef td);
                termEntry = td?.Id;
                NarrativeEntryRecord tr = td != null ? NarrativeService.Find(s, td.Id) : null;
                termOk = tech > 0 && tr != null && !tr.Read && tr.Act == termAct && td.Source == NarrativeCatalog.SourceTerminal && CountOf(NarrativeService.NotifyType) == n0 + 1
                         && NarrativeService.BodyOf(td) == HomeExplorationService.LoreFor(ti);
                HomeExplorationService.CompleteRecovery(s, new WorkOrderRecord { TargetId = term.Id });
                termOk &= NarrativeService.Find(s, td?.Id) == tr && CountOf(NarrativeService.NotifyType) == n0 + 1;
            }
            // 审查修复（P1）：家园遗迹点（真实回收完工入口）的背景资料收进资料库“数据终端”（家园不属任何领地，按收到时的幕归档），重复完工不重复收。
            bool relicOk = false;
            string relicEntry = null;
            feats.Clear();
            WorldFeatures.Query(ctx, -80, -80, 80, 80, feats);
            WorldFeature relic = feats.FirstOrDefault(f => f.Kind == WorldFeatureKind.Relic);
            if (relic != null)
            {
                HomeGridService.RevealArea(s, new Vector2(relic.X, relic.Y), 6f);
                HomeExplorationService.TryGetRelic(s, relic.Id, out HomeExplorationService.RelicInfo ri);
                string rkey = HomeExplorationService.LoreKeyFor(ri);
                int rn0 = CountOf(NarrativeService.NotifyType);
                int rc0 = NarrativeService.Count(s);
                HomeExplorationService.CompleteRecovery(s, new WorkOrderRecord { TargetId = relic.Id });
                NarrativeCatalog.TryGetByTrigger("terminal:" + rkey, out NarrativeEntryDef rd);
                relicEntry = rd?.Id;
                NarrativeEntryRecord rr = rd != null ? NarrativeService.Find(s, rd.Id) : null;
                relicOk = rkey.StartsWith("relic.lore.", StringComparison.Ordinal) && rr != null && !rr.Read && rr.Act == Math.Max(1, s.Progress.Act)
                          && rd.Source == NarrativeCatalog.SourceTerminal && NarrativeService.BodyOf(rd) == HomeExplorationService.LoreFor(ri)
                          && CountOf(NarrativeService.NotifyType) == rn0 + 1 && NarrativeService.Count(s) == rc0 + 1;
                HomeExplorationService.CompleteRecovery(s, new WorkOrderRecord { TargetId = relic.Id });
                relicOk &= NarrativeService.Count(s) == rc0 + 1 && CountOf(NarrativeService.NotifyType) == rn0 + 1;
            }
            // 审查修复（P1）：到达事务先投放、地点载入失败时撤回——只撤回这一趟新收的，之前到过（已收录）的不动。
            int rv0 = NarrativeService.Count(s);
            GameEvent.Send(ExpeditionMarchService.SiteArrivedEvent, FracturedCityLayout.RegionId);
            bool filedPre = NarrativeService.Has(s, "node.sky_eyes") && NarrativeService.Count(s) == rv0 + 1;
            GameEvent.Send(ExpeditionMarchService.SiteArrivalRevertedEvent, FracturedCityLayout.RegionId);
            bool revoked = !NarrativeService.Has(s, "node.sky_eyes") && NarrativeService.Count(s) == rv0;
            GameEvent.Send(ExpeditionMarchService.SiteArrivedEvent, FracturedCityLayout.RegionId);
            GameEvent.Send(ExpeditionMarchService.SiteArrivedEvent, FracturedCityLayout.RegionId); // 第二次到达：不新收
            GameEvent.Send(ExpeditionMarchService.SiteArrivalRevertedEvent, FracturedCityLayout.RegionId); // 第二次到达载入失败：不能撤掉第一次收的
            bool keptOld = NarrativeService.Has(s, "node.sky_eyes") && NarrativeService.Count(s) == rv0 + 1;
            Expect(relicOk, $"J3 遗迹点：回收家园遗迹点（真实完工入口）→ 背景资料收进“{relicEntry}”（数据终端、未读、正文就是遗迹记录）并通知；重复完工不重复收 {relicOk}" + (relic == null ? "（附近没找到遗迹点）" : string.Empty));
            Expect(filedPre && revoked && keptOld, $"J3 到达撤回：到达先投放 {filedPre}；地点载入失败撤回这一趟新收的 {revoked}；之前已收录的再到达失败不被撤掉 {keptOld}");
            // 监听截获（真实情报产出：第二幕起数据核心附带的情报会挑舰队片段）→ 资料库“监听截获”，正文是“无线电杂音 + 文字”。
            s.Progress.Act = 2;
            for (int i = 0; i < 8 && (s.Research.Intel?.FragmentsHeard?.Length ?? 0) == 0; i++)
            {
                IntelService.GrantFromDataCore(s);
            }
            string frag = s.Research.Intel?.FragmentsHeard?.FirstOrDefault();
            NarrativeEntryDef fd = frag != null && NarrativeCatalog.TryGetByTrigger("intel:" + frag, out NarrativeEntryDef d0) ? d0 : null;
            NarrativeEntryRecord fr = fd != null ? NarrativeService.Find(s, fd.Id) : null;
            string body = NarrativeService.BodyOf(fd);
            bool intelOk = fr != null && fr.Act == 2 && body.StartsWith(GameText.Get("narrative.intercept.noise"), StringComparison.Ordinal) && body.Contains(GameText.Get(fd.BodyKey));
            // 核心备忘（归还里程碑 / 领地收复发的 GameEvent，与 PurificationService / TerritoryControlService 同一事件）：收进资料库，不再重复发通知（来源系统已发“核心备忘”）。
            int nn = CountOf(NarrativeService.NotifyType);
            GameEvent.Send(PurificationService.CoreMemoEvent, "m5000");
            GameEvent.Send(PurificationService.CoreMemoEvent, TerritoryControlService.MemoPrefix + "silent");
            GameEvent.Send(PurificationService.CoreMemoEvent, "m5000");
            NarrativeEntryRecord mr = NarrativeService.Find(s, "memo.m5000");
            bool memoOk = mr != null && mr.Act == 2 && NarrativeService.Has(s, "memo.reclaimed.silent") && CountOf(NarrativeService.NotifyType) == nn
                          && (s.Progress.Narrative.Count(r => r.Id == "memo.m5000") == 1);
            Expect(termOk, $"J3 数据终端：读取静默领地的旧终端（真实完工入口）→ 收进“{termEntry}”（未读、第一幕、正文就是终端记录）并通知；再读一次不重复 {termOk}");
            Expect(intelOk, $"J3 监听截获：第二幕起真实情报产出截获舰队片段 {frag} → 收进资料库（第二幕），正文“无线电杂音 + 文字” {intelOk}");
            Expect(memoOk, $"J3 核心备忘：归还里程碑 / 领地收复的备忘事件收进资料库（按收到时的幕 = 第 {mr?.Act} 幕），不重复发通知、同一条只收一次 {memoOk}");
        }

        // ── N2 负向矩阵 ───────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            CampaignState a = NewGame(9141);
            _player.Tick(0.016f);
            PlayToEnd();
            TerritoryControlService.MarkLeaderDefeated(a, "silent");
            int countA = NarrativeService.Count(a);
            WorldSimulation.SyncAllForSave();
            SaveResult savedA = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            int notes = CountOf(NarrativeService.NotifyType);
            // 幂等：同一触发再来一次（直接投放与事件两条路）不重复、不再通知。
            bool idem = !NarrativeService.Trigger(a, "leader:silent") && NarrativeService.Count(a) == countA && CountOf(NarrativeService.NotifyType) == notes;
            GameEvent.Send(TerritoryControlService.LeaderDefeatedEvent, "silent");
            idem &= NarrativeService.Count(a) == countA;
            // 坏触发 / 未知条目：不投放、不抛异常。
            bool bad = !NarrativeService.Trigger(a, "leader:nobody") && !NarrativeService.Trigger(a, "") && !NarrativeService.Trigger(a, null) && !NarrativeService.Trigger(null, "new_game")
                       && NarrativeService.Count(a) == countA;
            GameEvent.Send(NarrativeService.FeedEvent, "terminal:terminal.lore.nonexistent");
            bad &= NarrativeService.Count(a) == countA;
            // 待播条目已不在表里（旧档 / 表删行）：开始播放时清掉待播，不卡住、不接管画面。
            a.Progress.PendingCutscene = "node.removed_in_new_version";
            NarrativeCutscene ghost = NarrativeService.BeginPending(a);
            _player.Tick(0.016f);
            bool ghostOk = ghost == null && a.Progress.PendingCutscene.Length == 0 && !NarrativeCutscenePlayerUIToolkit.IsShowing && !GameRoot.IsWorldPaused;
            // 重看不需要确认、不改存档；资料库里没有的演出不能重看。
            bool noReplayUnknown = NarrativeService.BeginReplay(a, "node.sky_eyes") == null && NarrativeService.BeginReplay(a, "node.what_waits") == null;
            string snapA = JsonUtility.ToJson(a.Progress);
            NarrativeCutscene replay = NarrativeService.BeginReplay(a, NarrativeService.WakeId);
            _player.Show(replay);
            bool replayShown = NarrativeCutscenePlayerUIToolkit.IsShowing && _player.ReplayTagVisible && !NarrativeService.SkipNeedsConfirm && GameRoot.IsWorldPaused;
            Click(_player.SkipButton);
            bool replayOk = replayShown && !UiConfirmDialog.IsOpen && !NarrativeCutscenePlayerUIToolkit.IsShowing && JsonUtility.ToJson(a.Progress) == snapA && !GameRoot.IsWorldPaused
                            && GameSettings.HasSeenGuidanceHook(GuidanceHooks.NarrativeFirstReplay);
            // 播放中战役被换掉（读档 / 回主菜单）：中止，不收录，世界暂停状态恢复。
            NarrativeService.BeginReplay(a, NarrativeService.WakeId);
            _player.Show(NarrativeService.Playing);
            CampaignState other = CampaignState.CreateNew("fgnarrative-other", "Standard", 9142);
            CampaignSession.Set(SlotB, other);
            _player.Tick(0.016f);
            bool swapped = !NarrativeCutscenePlayerUIToolkit.IsShowing && NarrativeService.Playing == null && NarrativeService.Count(other) == 0 && !GameRoot.IsWorldPaused;
            CampaignSession.Set(Slot, a);
            // 新存档看不到其他存档的剧情（FGR-UX-051 / FG9-UX-01 负向）：另一槽位新游戏的资料库是空的，只有它自己的开场待播。
            CampaignState b = NewGame(9143, SlotB);
            bool fresh = NarrativeService.Count(b) == 0 && b.Progress.PendingCutscene == NarrativeService.WakeId && NarrativeService.UnreadCount(b) == 0;
            // 读回 A：A 的资料库原样（按存档保存）。
            _player.Tick(0.016f);
            PlayToEnd();
            LoadResult la = CampaignSaveService.Load(Slot);
            // 旧档缺资料库字段（叙事框架之前的 v2 存档）：读档补成空资料库，不是 null。
            CampaignState old = CampaignState.CreateNew("fgnarrative-old", "Standard", 9144);
            old.Progress.Narrative = null;
            old.Progress.PendingCutscene = null;
            old.Progress.NarrativeSerial = 0;
            CampaignFgStateDomains.EnsureAll(old);
            bool oldOk = old.Progress.Narrative != null && old.Progress.Narrative.Length == 0 && old.Progress.PendingCutscene == string.Empty && old.Progress.NarrativeSerial == 1;
            // 已经玩过的存档（统一时钟走过）进入战役：不补播开场、不改资料库（回滚 / 读档前后一致）。
            old.Clock.Ticks = 600;
            string oldSnap = JsonUtility.ToJson(old.Progress);
            NarrativeService.OnCampaignEntered(old);
            bool playedNoIntro = JsonUtility.ToJson(old.Progress) == oldSnap;
            Expect(idem && bad, $"N2 同一触发再来（直接投放 / 事件）不重复、不再通知 {idem}；坏触发 / 未知终端记录 / 空战役不投放、不抛异常 {bad}");
            Expect(ghostOk, $"N2 待播的演出已不在表里：开始播放时清掉待播、不接管画面、世界不暂停 {ghostOk}");
            Expect(noReplayUnknown && replayOk, $"N2 资料库里没有的演出不能重看 {noReplayUnknown}；重看带“重看”标记、跳过不需要确认、不改存档、世界恢复 {replayOk}");
            Expect(swapped, $"N2 播放中战役被换掉：演出中止、不收录、世界恢复 {swapped}");
            Expect(fresh && savedA.Success && la.Success && NarrativeService.Count(la.State) == countA && NarrativeService.Has(la.State, "node.first_fragment"),
                $"N2 资料库按存档保存：另一槽位的新游戏资料库为空（只有自己的开场待播）{fresh}；读回原存档，资料库原样（{NarrativeService.Count(la.State)} 条）");
            Expect(oldOk && playedNoIntro, $"N2 旧档缺资料库字段 → 补成空资料库 {oldOk}；已经玩过的存档进入战役不补播开场、不改资料库 {playedNoIntro}");
        }

        // ── T1 暂停与 0.5x～3x ────────────────────────────────────────────────────

        private static void CheckTimingMatrix()
        {
            GameSettings.ResetNarrativeText();
            var lines = new List<string>();
            bool allSame = true;
            string reference = null;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                foreach (bool pausedFirst in new[] { false, true })
                {
                    CampaignState s = NewGame(9151);
                    GameClock.SetSpeed(speed);
                    GameRoot.SetWorldPaused(pausedFirst);
                    long ticks0 = GameClock.Ticks;
                    _player.Tick(0.016f);
                    var trace = new StringBuilder();
                    for (int i = 0; i < 12; i++)
                    {
                        _player.Tick(0.05f);
                        trace.Append(_player.LineText.Length).Append(',');
                    }
                    bool pausedDuring = GameRoot.IsWorldPaused;
                    bool ended = PlayToEnd(0.25f);
                    bool restored = GameRoot.IsWorldPaused == pausedFirst && Math.Abs(GameClock.Speed - speed) < 1e-4f && NarrativeService.Has(s, NarrativeService.WakeId);
                    string t = trace.ToString();
                    reference ??= t;
                    bool same = t == reference;
                    allSame &= same && pausedDuring && ended && restored;
                    lines.Add($"{speed}x{(pausedFirst ? "+先暂停" : string.Empty)}：{(same && pausedDuring && ended && restored ? "一致" : $"不一致（{t} 暂停 {pausedDuring} 结束 {ended} 恢复 {restored}）")}");
                    GameRoot.SetWorldPaused(false);
                    GameClock.SetSpeed(1f);
                }
            }
            Expect(allSame, $"T1 暂停与 0.5x～3x：演出逐字节奏只按真实秒（同样的帧间隔每个倍速 / 先暂停与否字数轨迹逐帧相同 {reference}），播放中世界暂停，结束后恢复播放前的暂停与倍速；{string.Join("；", lines)}");
            // 阅读资料库不自动暂停（FG11 第 8 节），世界照常推进。
            CampaignState w = NewGame(9152);
            _player.Tick(0.016f);
            PlayToEnd();
            VisualElement root = F.MountUxml(UiKitFolder + "NarrativeLibraryPanel.uxml", out GameObject go);
            try
            {
                var panel = go.AddComponent<NarrativeLibraryPanelUIToolkit>();
                panel.BindView(root);
                NarrativeLibraryPanelUIToolkit.InWorldOverrideForTests = true;
                NarrativeLibraryPanelUIToolkit.Open(null);
                long t0 = GameClock.Ticks;
                bool notPaused = NarrativeLibraryPanelUIToolkit.IsOpen && !GameRoot.IsWorldPaused;
                WorldSimulation.StepMany(Hz);
                bool advanced = GameClock.Ticks == t0 + Hz;
                NarrativeLibraryPanelUIToolkit.Close();
                Expect(notPaused && advanced, $"T1 阅读资料库时游戏不自动暂停：打开资料库后世界仍在运行 {notPaused}，统一时钟照常前进 {advanced}");
            }
            finally
            {
                NarrativeLibraryPanelUIToolkit.Close();
                Object.DestroyImmediate(go);
            }
            Line("  · T2 观察一致：叙事投放只由事件触发（到达 / 首领 / 终端 / 截获 / 备忘），不在家园后台逐帧模拟，没有“观察时与不观察时”两条路径——不适用家园观察一致（理由写进证据）");
        }

        // ── S1 真文件存读档 ───────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewGame(9161);
            _player.Tick(0.016f);
            PlayToEnd();
            TerritoryControlService.MarkLeaderDefeated(s, "silent");
            GameEvent.Send(PurificationService.CoreMemoEvent, "m5000");
            NarrativeService.MarkRead(s, "node.first_fragment");
            string before = JsonUtility.ToJson(s.Progress);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            // 存档后接着收到一条（击败铸造主核心）。
            TerritoryControlService.MarkLeaderDefeated(s, "foundry");
            string continuous = Ids(s) + "|act=" + s.Progress.Act + "|serial=" + s.Progress.NarrativeSerial;
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
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
            NarrativeService.OnCampaignEntered(l);
            string loaded = JsonUtility.ToJson(l.Progress);
            bool readKept = NarrativeService.Find(l, "node.first_fragment")?.Read == true && NarrativeService.Find(l, "memo.m5000")?.Read == false && NarrativeService.Find(l, NarrativeService.WakeId)?.Read == true;
            TerritoryControlService.MarkLeaderDefeated(l, "foundry");
            string after = Ids(l) + "|act=" + l.Progress.Act + "|serial=" + l.Progress.NarrativeSerial;
            Expect(save.Success && loaded == before && readKept && after == continuous,
                $"S1 真文件存读档：资料库（条目、先后、幕、日、已读 / 未读）、待播、序号读回逐字段一致 {loaded == before}；已读状态保留 {readKept}；读档后接着收到的与一直跑一致（{after}）" +
                (loaded == before ? string.Empty : $"\n    存前 {before}\n    读回 {loaded}") + (after == continuous ? string.Empty : $"\n    一直跑 {continuous}"));
        }

        // ── U1 界面 ───────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewGame(9171);
            _player.Tick(0.016f);
            PlayToEnd();
            TerritoryControlService.MarkLeaderDefeated(s, "silent");
            GameEvent.Send(PurificationService.CoreMemoEvent, "m5000");
            VisualElement root = F.MountUxml(UiKitFolder + "NarrativeLibraryPanel.uxml", out GameObject go);
            VisualElement proot = F.MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject pgo);
            NarrativeLibraryPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<NarrativeLibraryPanelUIToolkit>();
                panel.BindView(root);
                var pause = pgo.AddComponent<PauseMenuUIToolkit>();
                pause.BindView(proot);
                InputRouter.Reset();
                int unread = NarrativeService.UnreadCount(s);

                // 入口一：暂停菜单“资料库（N 条未读）”→ 盖在暂停菜单上面打开，选中最新一条未读。
                pause.SetOpen(true);
                bool menuText = pause.LibraryButton != null && pause.LibraryButton.text == GameText.Format("pause.library_unread", unread);
                Click(pause.LibraryButton);
                bool opened = NarrativeLibraryPanelUIToolkit.IsOpen && panel.PanelVisible && panel.CurrentTab == NarrativeLibraryPanelUIToolkit.TabEntries
                              && panel.Selected == "memo.m5000" && GameSettings.HasSeenGuidanceHook(GuidanceHooks.NarrativeLibraryFirstOpen);
                bool listOk = panel.RowCount == NarrativeService.Count(s) && panel.RowId(0) == "memo.m5000" && panel.RowText(1).StartsWith(GameText.Get("narrative.library.unread_mark"), StringComparison.Ordinal)
                              && panel.RowUnread(1) && !panel.RowUnread(0) && panel.DetailTitleText == GameText.Get("purify.memo.m5000.title")
                              && panel.DetailMetaText.Contains(GameText.Get("narrative.source.memo")) && NarrativeService.Find(s, "memo.m5000").Read
                              && panel.TabEntriesText == GameText.Format("narrative.library.tab.entries", unread - 1);
                // 点一行阅读 → 标为已读；监听截获带杂音；演出条目有“重看演出”。
                int fragRow = Enumerable.Range(0, panel.RowCount).First(i => panel.RowId(i) == "node.first_fragment");
                Click(panel.RowButton(fragRow));
                bool readFrag = panel.Selected == "node.first_fragment" && NarrativeService.Find(s, "node.first_fragment").Read && panel.DetailBodyText.StartsWith(GameText.Get("narrative.intercept.noise"), StringComparison.Ordinal)
                                && !panel.ReplayVisible;
                // 筛选：来源“演出”只剩“苏醒”；幕“第二幕”为空（筛选后为空的说明）；回到全部。
                Click(panel.SourceButton(4));
                bool srcFilter = panel.SourceFilter == NarrativeCatalog.SourceCutscene && panel.RowCount == 1 && panel.RowId(0) == NarrativeService.WakeId;
                Click(panel.RowButton(0));
                bool replayBtn = panel.ReplayVisible;
                Click(panel.SourceButton(0));
                Click(panel.ActButton(2));
                bool actFilter = panel.ActFilter == 2 && panel.RowCount == 0 && panel.EmptyText == GameText.Get("narrative.library.empty_filter");
                Click(panel.ActButton(1));
                bool act1 = panel.RowCount == NarrativeService.Count(s);
                Click(panel.ActButton(0));
                // 剧情回顾：只列剧情节点，按先后；筛选栏收起。
                Click(panel.TabRecapButton);
                bool recap = panel.CurrentTab == NarrativeLibraryPanelUIToolkit.TabRecap && panel.RowCount == 2 && panel.RowId(0) == NarrativeService.WakeId && panel.RowId(1) == "node.first_fragment"
                             && !panel.FiltersVisible && panel.RowText(0).Contains(GameText.Get("narrative.node.wake.title"));
                Click(panel.TabEntriesButton);
                // 全部标为已读。
                Click(panel.MarkAllButton);
                bool allRead = NarrativeService.UnreadCount(s) == 0 && panel.TabEntriesText == GameText.Get("narrative.library.tab.entries_none") && !panel.MarkAllButton.enabledSelf;
                // 重看演出：资料库关闭、演出播放（带“重看”标记），跳过不确认，回到原状态。
                Click(panel.SourceButton(4));
                Click(panel.RowButton(0));
                Click(panel.ReplayButton);
                bool replaying = !NarrativeLibraryPanelUIToolkit.IsOpen && NarrativeCutscenePlayerUIToolkit.IsShowing && _player.ReplayTagVisible;
                UiEscapeStack.CloseTop();
                bool replayEnd = !NarrativeCutscenePlayerUIToolkit.IsShowing && !UiConfirmDialog.IsOpen;
                Click(panel.SourceButton(0));
                // 字幕大小跟随设置：资料库正文与演出字幕的字号。
                GameSettings.SetSubtitleScale(1.5f);
                NarrativeLibraryPanelUIToolkit.Open("node.first_fragment");
                bool font = Math.Abs(panel.DetailFontSize - UiTuningValues.Get("narrative.library_font_px") * 1.5f) < 0.01f;
                NarrativeLibraryPanelUIToolkit.Close();
                // 入口二：点“资料库新增”通知 → 打开资料库（最新一条未读）；“核心备忘”通知同样打开（选中最新的备忘）。
                TerritoryControlService.MarkLeaderDefeated(s, "foundry");
                NotificationEntry ne = NotificationCenter.History.LastOrDefault(e => e?.Type?.Id == NarrativeService.NotifyType);
                string newest = NarrativeService.NewestUnread(s);
                bool viaNotify = ne != null && NarrativeService.Has(s, "node.what_waits") && newest != null && NotificationCenter.HasOpenHandler(ne) && NotificationCenter.TryOpen(ne)
                                 && NarrativeLibraryPanelUIToolkit.IsOpen && panel.Selected == newest;
                NarrativeLibraryPanelUIToolkit.Close();
                NotificationEntry me = NotificationCenter.Post(NarrativeService.MemoNotifyType, "x");
                bool viaMemo = me != null && NotificationCenter.TryOpen(me) && NarrativeLibraryPanelUIToolkit.IsOpen && NarrativeCatalog.Find(panel.Selected)?.Source == NarrativeCatalog.SourceMemo;
                // Esc 关闭；“?”关掉资料库并打开图鉴条目。
                UiEscapeStack.CloseTop();
                bool escClosed = !NarrativeLibraryPanelUIToolkit.IsOpen;
                NarrativeLibraryPanelUIToolkit.Open(null);
                Click(panel.HelpButton);
                bool help = !NarrativeLibraryPanelUIToolkit.IsOpen;
                Expect(menuText && opened && listOk, $"U1 暂停菜单“{pause.LibraryButton?.text}”→ 资料库盖在上面打开、选中最新一条未读并标为已读 {opened}；列表最新在前、● 未读、详情写来源 / 幕 / 日 {listOk}");
                Expect(readFrag && srcFilter && replayBtn && actFilter && act1 && recap && allRead,
                    $"U1 点行阅读（监听截获带杂音）{readFrag}；来源筛选“演出”{srcFilter}、演出条目有“重看演出”{replayBtn}；第二幕为空的筛选说明 {actFilter}、第一幕全部 {act1}；剧情回顾按先后、筛选栏收起 {recap}；全部标为已读 {allRead}");
                Expect(replaying && replayEnd && font, $"U1 重看演出：资料库关闭、演出带“重看”标记播放 {replaying}，Esc 直接结束（不确认）{replayEnd}；资料库正文字号跟随字幕大小 {font}");
                Expect(viaNotify && viaMemo && escClosed && help, $"U1 点“资料库新增”通知打开到最新一条未读（{panel.Selected}）{viaNotify}；点“核心备忘”通知打开到最新备忘 {viaMemo}；Esc 关闭 {escClosed}；“?”先关资料库再开图鉴 {help}");

                // 暂停菜单“文本与字幕”：两个滑条立即生效、标签写出当前值，恢复默认。
                GameSettings.ResetNarrativeText();
                pause.SyncNarrativeText();
                pause.TextSpeedSlider.value = 20f;
                pause.SubtitleScaleSlider.value = 1.2f;
                bool sliders = Math.Abs(GameSettings.NarrativeTextSpeed - 20f) < 0.01f && Math.Abs(GameSettings.SubtitleScale - 1.2f) < 0.01f
                               && pause.TextSpeedLabelText == GameText.Format("pause.text_speed", "20") && pause.SubtitleScaleLabelText == GameText.Format("pause.subtitle_scale", "120");
                pause.TextSpeedSlider.value = GameSettings.NarrativeTextSpeedMax;
                bool instantLabel = GameSettings.NarrativeTextInstant && pause.TextSpeedLabelText == GameText.Get("pause.text_speed_instant");
                Click(pause.TextResetButton);
                bool reset = Math.Abs(GameSettings.NarrativeTextSpeed - GameSettings.NarrativeTextSpeedDefault) < 0.01f && Math.Abs(GameSettings.SubtitleScale - GameSettings.SubtitleScaleDefault) < 0.01f
                             && Math.Abs(pause.TextSpeedSlider.value - GameSettings.NarrativeTextSpeedDefault) < 0.01f;
                // 设置跨战役保存（本机偏好）：重新载入设置后仍在。
                GameSettings.SetSubtitleScale(1.4f);
                GameSettings.Load();
                bool persisted = Math.Abs(GameSettings.SubtitleScale - 1.4f) < 0.01f;
                pause.SetOpen(false);
                Expect(sliders && instantLabel && reset && persisted,
                    $"U1 暂停菜单“文本与字幕”：滑条立即生效（“{pause.TextSpeedLabelText}”）{sliders}；拉到最大 = 立即显示 {instantLabel}；恢复默认 {reset}；设置跨战役保存 {persisted}");

                // 英文：没有缺失键标记；空资料库状态。
                GameSettings.SetLanguage(GameLanguage.En);
                CampaignState empty = CampaignState.CreateNew("fgnarrative-empty", "Standard", 9172);
                CampaignSession.Set(SlotB, empty);
                NarrativeLibraryPanelUIToolkit.Open(null);
                bool emptyState = panel.RowCount == 0 && panel.EmptyText == GameText.Get("narrative.library.empty") && panel.DetailBodyText == GameText.Get("narrative.library.detail_hint");
                string texts = panel.TitleText + panel.CountText + panel.EmptyText + panel.FooterText + panel.TabEntriesText;
                bool noMarker = !texts.Contains("⟦") && texts.Contains("Archive");
                NarrativeLibraryPanelUIToolkit.Close();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSession.Set(Slot, s);
                Expect(emptyState && noMarker, $"U1 空资料库（新存档）显示说明 {emptyState}；英文界面无缺失键标记 {noMarker}");

                // 布局探针（四种分辨率 + 超长文字 + USS 体检）。
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "NarrativeLibraryPanel.uxml", "NlWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("NlRoot")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<Button>("NlReplay")?.RemoveFromClassList("uk-hidden");
                });
                string probe2 = UiToolkitLayoutProbe.Probe(UiKitFolder + "NarrativeCutscene.uxml", "NcRoot", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("NcRoot")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<Label>("NcTag")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "U1 资料库布局探针：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
                Expect(probe2.Contains("PASS") && !probe2.Contains("FAIL"), "U1 演出播放器布局探针：" + (probe2.Contains("FAIL") ? probe2 : probe2.Split('\n')[0]));
            }
            finally
            {
                NarrativeLibraryPanelUIToolkit.Close();
                NarrativeLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                GameSettings.ResetNarrativeText();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(pgo);
            }
        }

        // ── P1 性能 ──────────────────────────────────────────────────────────────

        private static void Perf(string name, double ms, double budget, string note)
        {
            string cpu = $"测量环境 Editor batchmode / Mono JIT，{SystemInfo.processorCount} 线程";
            if (ms <= budget)
            {
                Expect(true, $"P1 {name} {ms:0.0000} ms ≤ {budget} ms（{note}；{cpu}）");
            }
            else if (ms <= budget * 2)
            {
                Expect(true, $"P1 {name} {ms:0.0000} ms（性能警告：超过 {budget} ms 但不到 2 倍；{note}；{cpu}）");
                PerfLines.Add($"警告 {name} {ms:0.0000} ms > {budget} ms");
            }
            else
            {
                Fail($"P1 {name} {ms:0.0000} ms 超过 {budget} ms 的 2 倍（{note}；{cpu}）");
            }
            PerfLines.Add($"{name} {ms:0.0000} ms（预算 {budget} ms）");
        }

        private static void CheckPerformance()
        {
            CampaignState s = CampaignState.CreateNew("fgnarrative-perf", "Standard", 9181);
            CampaignSession.Set(SlotB, s);
            // 把全部非演出条目收进资料库（本存档最多几十条）。
            foreach (NarrativeEntryDef d in NarrativeCatalog.All.Where(d => !d.IsCutscene))
            {
                NarrativeService.Deliver(s, d);
            }
            int n = 2000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                NarrativeService.Trigger(s, "leader:silent");
            }
            sw.Stop();
            Perf($"投放一次（已收过的幂等路径，资料库 {NarrativeService.Count(s)} 条）", sw.Elapsed.TotalMilliseconds / n, 0.02, "只在事件发生时");
            var list = new List<NarrativeEntryRecord>();
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                NarrativeService.Query(s, i % 4, i % 2 == 0 ? null : NarrativeCatalog.SourceTerminal, list);
                NarrativeService.Recap(s, list);
                NarrativeService.UnreadCount(s);
            }
            sw.Stop();
            Perf("资料库筛选 + 剧情回顾 + 未读数一次", sw.Elapsed.TotalMilliseconds / n, 0.1, "只在面板刷新时（版本变化才重建）");
            NarrativeService.Trigger(s, "new_game");
            NarrativeCutscene c = new NarrativeCutscene(NarrativeCatalog.Find(NarrativeService.WakeId), replay: true);
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                c.Tick(0.001f, 40f, false);
                c.VisibleText(40f, false);
            }
            sw.Stop();
            Perf("演出每帧推进 + 取逐字文字", sw.Elapsed.TotalMilliseconds / n, 0.02, "只在播放演出时");
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgNarrativeSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                UiConfirmDialog.DiscardAll();
                NarrativeService.AbortPlaying();
                if (_player != null && NarrativeCutscenePlayerUIToolkit.IsShowing)
                {
                    _player.Tick(0.016f);
                }
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
