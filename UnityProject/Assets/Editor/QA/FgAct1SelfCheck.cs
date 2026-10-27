using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
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
using GameLogic.Narrative;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using BinGames.Sim.WorldGen;
using UnityEditor;
using UnityEngine;
using GameLogic.EditorTools.JourneyBots;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG9-ACT1-01 第一幕流程、解锁节奏与伏笔（FG16 第 2 节 0:00～8:00；FG11 FGR-NAR-010 前五个节点；FG13 FGR-UX-040；承接 DEBT-FG0ARCH01-01 / FG8EXP03-06 /
    /// FG9FAC01-02 / FG9FAC02-02 / FG8EXP05-01 / FG7ENV02-03 / FG9NAR01-04）的自检——起真实系统跑，行为坏掉就会失败：
    /// A 数据：节奏台账 14 个节奏点按 FG16 排序（0:00～8:00）、钩子都在 GuidanceHooks.Known、图鉴 / 剧情节点都存在、主线节奏点的条件不引用可跳过的系统（负向“跳过不影响主线”的静态保证）、
    ///   传送带 T1 由节奏点卡住；伏笔分寸（第一幕剧情文本中英文都没有揭示词、四个伏笔节点都有母题词）。
    /// B 节奏台账：新游戏 0:00 记“苏醒”、传送带锁定 → 修好发电机 / 第一台履带机 → 进入破碎都市（远征中仍锁定）→ 回到家园才开放传送带（新系统通知、图鉴、指引任务）；
    ///   事件型节奏点（建炮塔钩子）当场记、无关钩子不记、重复不重记；首领 / 幕；跳过全部可选系统主线照样走到第一幕结束；暂停不推进、0.5x～3x 到达在同一步；
    ///   存读档逐字段一致；旧档迁移补记（不通知）；旅程旧规则开关；统计面板段。
    /// C 远征地点的生成内容：3 个种子两个地点的驻军都在 Demo 矩形外、与入口连通、地形障碍取自星球真实地形；登记成 kind = site 据点并计入控制度；
    ///   星球上不迎战展开、不作突袭出发地；进场播种（Demo 编成原样）、部分击杀不摧毁、全灭 = 摧毁（控制度下降、掉落在地点里、走近自动拾取）；
    ///   地点载入时不重建、卸载后重建并换一组实例；存读档不重复播种；铸造前哨熔渣热区进内核且在 Demo 矩形外、驻军步进炮不掉关键物；旧世界（v2）保持 Demo 原样。
    /// D 诱饵中继假信号点：诱饵按种子放在静默领地、远离锚点；远征队附近侦测（没有队伍不侦测）→ 地图上真假一模一样；反制预览情报顺带甄别 → 地图分开写；揭开迷雾的诱饵写“已识破”；存读档。
    /// E 第二幕的可见变化（DEBT-FG9NAR01-04）：“它在等什么”收到后进入第 2 幕 → 第二幕领地的主线任务出现、舰队片段情报变成“需要”。派遣检查单的静默夜提醒按路线估时（DEBT-FG7ENV02-03）。
    /// F 性能（Editor batchmode，一次测量）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgAct1SelfCheck）；单段：bash tools/unity-validate.sh --segment FgAct1SelfCheck。
    /// </summary>
    public static class FgAct1SelfCheck
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
        private static readonly List<string> Touched = new List<string>(32);

        [MenuItem("BinGames/QA/自检/FG 第一幕流程、解锁节奏与伏笔")]
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
            Line("\n[第一幕流程、解锁节奏与伏笔] 节奏台账与开放门槛、远征地点生成内容、诱饵中继假信号点、伏笔、第二幕变化（FG9-ACT1-01）");
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
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgact1-selfcheck-" + Guid.NewGuid().ToString("N"));
            GuidanceHooks.Touched += OnTouched;
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
                // 修复轮：图鉴进度写到本段的临时目录（不读、不改这台机器上的图鉴进度），B1“开局图鉴未解锁”才有确定的起点。
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex_discovered.json");
                MechanicCodex.Reload();
                GameRoot.BindWorldProviders();
                NarrativeService.EnsureListening();
                Act1PacingService.EnsureListening();
                SiteGenLayout.ResetCache();
                SignalContactService.ResetSessionState();
                SlagHeatService.ResetForTests();
                // 本段测节奏门槛本身：按真实规则跑（全量自检的旧段开关在 finally 里还原）。
                ResearchGate.LegacyGatesOpenForTests = false;
                Act1PacingService.LegacyGatesOpenForJourneys = false;

                Step(CheckData);
                Step(CheckForeboding);
                Step(CheckPacingFlow);
                Step(CheckHookAndLeaderPoints);
                Step(CheckSkipOptional);
                Step(CheckPauseAndSpeed);
                Step(CheckPacingSaveLoadAndMigration);
                Step(CheckSiteLayoutSeeds);
                Step(CheckGarrisonRegistration);
                Step(CheckGarrisonFight);
                Step(CheckGarrisonRebuildAndSave);
                Step(CheckFoundrySite);
                Step(CheckGarrisonReachByCommands);
                Step(CheckFoundryCoreBand);
                Step(CheckEarlyRelayEvaluation);
                Step(CheckOldWorld);
                Step(CheckDecoyContacts);
                Step(CheckActTwoAndSilentEstimate);
                Step(CheckPerf);
            }
            finally
            {
                GuidanceHooks.Touched -= OnTouched;
                MechanicCodex.FilePathOverrideForTests = null;
                MechanicCodex.Reload();
                ResearchGate.LegacyGatesOpenForTests = legacy;
                Act1PacingService.LegacyGatesOpenForJourneys = false;
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
            Line(_fail == 0 ? $"  第一幕流程、解锁节奏与伏笔：全部 {_pass} 项通过" : $"  第一幕流程、解锁节奏与伏笔：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        private static void OnTouched(string hookId) => Touched.Add(hookId);

        // ───────────────────────────── A 数据 ─────────────────────────────

        private static void CheckData()
        {
            IReadOnlyList<ActPacing> rows = Act1PacingService.AllRows();
            bool sorted = rows.Count == 14 && rows.Select(r => r.SortOrder).SequenceEqual(rows.Select(r => r.SortOrder).OrderBy(x => x))
                          && Math.Abs(rows[0].Minute) < 0.01f && Math.Abs(rows[rows.Count - 1].Minute - 480f) < 0.01f
                          && rows.Zip(rows.Skip(1), (a, b) => a.Minute <= b.Minute).All(x => x);
            List<string> badHook = rows.Where(r => !GuidanceHooks.Known.Contains(r.HookId)).Select(r => r.Id).ToList();
            List<string> badCodex = rows.SelectMany(r => r.CodexIds.Split(',')).Where(c => MechanicCodex.Find(c.Trim()) == null).ToList();
            List<string> badNarr = rows.Where(r => r.NarrativeId != "none" && NarrativeCatalog.Find(r.NarrativeId) == null).Select(r => r.Id).ToList();
            Expect(sorted && badHook.Count == 0 && badCodex.Count == 0 && badNarr.Count == 0,
                $"A1 fg.TbActPacing：{rows.Count} 个节奏点按 FG16 第 2 节排好（0:00 苏醒 … 8:00 第一幕结束，目标时刻不减）；每个都有引导钩子（在 GuidanceHooks.Known，缺 {string.Join(",", badHook)}）、" +
                $"图鉴条目（缺 {string.Join(",", badCodex)}）、剧情节点（缺 {string.Join(",", badNarr)}）——“每个新系统开放时都有引导和图鉴”");

            // 负向“跳过某个系统不影响主线推进”的静态保证：主线节奏点的条件只读主线事实（不读可跳过系统的钩子 / 建筑）。
            var optionalFacts = new HashSet<string>(rows.Where(r => r.Mainline == 0).SelectMany(r => r.Condition.Split('|')).Select(c => c.Trim()), StringComparer.Ordinal);
            List<string> leaks = rows.Where(r => r.Mainline == 1).SelectMany(r => r.Condition.Split('|')).Select(c => c.Trim()).Where(optionalFacts.Contains).ToList();
            string[] mainline = rows.Where(r => r.Mainline == 1).Select(r => r.Id).ToArray();
            Expect(leaks.Count == 0 && mainline.SequenceEqual(new[] { "wake", "power", "first_machine", "first_expedition", "foundry_outer", "silent_mind", "foundry_core", "act1_end" }),
                $"A2 主线节奏点（{string.Join(" → ", mainline)}）的条件不引用任何可跳过系统的事实（泄漏 {leaks.Count}）：接入 / 炮塔 / 突袭 / 静默夜 / 超控阵列没到也挡不住主线");

            BuildCatalog.TryGet("belt_t1", out BuildEntry belt);
            ActPacing beltsRow = Act1PacingService.Row("belts_open");
            Expect(belt != null && belt.UnlockRule == "pace:belts_open" && beltsRow != null && beltsRow.Opens == "build:belt_t1" && beltsRow.Condition.Contains("site_back:" + City)
                   && GameText.Has(belt.UnlockHintKey) && QuestCatalog.Rows.Any(q => q.Id == "guide.belt" && q.Unlock == QuestCatalog.UnlockPace && q.Prereq == "belts_open"),
                $"A3 设计案 6.5“第一幕在第一次远征之后开放基础传送带”：传送带 T1 的解锁规则 {belt?.UnlockRule}、节奏点 belts_open 的条件“{beltsRow?.Condition}”、锁定提示“{(belt != null ? GameText.Get(belt.UnlockHintKey) : "")}”；传送带指引随之出现（出现时机 pace）");

            TbSiteGen table = ConfigSystem.Instance.Tables.TbSiteGen;
            Expect(table != null && table.DataList.Count == 2 && table.DataList.All(r => r.Garrisons > 0 && r.HalfX > r.DemoHalfX + r.Margin && r.MinY < r.DemoMinY - r.Margin && r.MaxY > r.DemoMaxY + r.Margin)
                   && table.DataList.Single(r => r.Site == Fo).SlagZones > 0 && table.DataList.Single(r => r.Site == City).SlagZones == 0,
                $"A4 fg.TbSiteGen：两个远征地点（破碎都市 / 铸造前哨）的窗口每边都比 Demo 布局大一个 margin；只有铸造前哨有熔渣热区");
        }

        private static void CheckForeboding()
        {
            string[] banned = { "净化协议", "清除全部自治单元", "方舟舰队", "白潮", "归乡号", "清洗", "背叛", "Purge Protocol", "Ark Fleet", "White Tide", "Homecoming", "betray" };
            string[] motifs = { "天空", "天上", "轨道", "返航", "清点", "等待", "等着", "在等" }; // 修复轮：去掉单字“等”（与 fgdata_act1.FOREBODING_MOTIFS 一致）
            var hits = new List<string>();
            int texts = 0;
            foreach (NarrativeEntryDef def in NarrativeCatalog.All.Where(d => d.Act == 1))
            {
                var keys = new List<string> { def.TitleKey, def.BodyKey };
                keys.AddRange(def.Lines.Select(l => l.TextKey));
                foreach (string k in keys)
                {
                    foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                    {
                        string t = GameText.Get(k, lang) ?? string.Empty;
                        texts++;
                        hits.AddRange(banned.Where(w => t.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).Select(w => $"{k}:{w}"));
                    }
                }
            }
            foreach (ActPacing r in Act1PacingService.AllRows())
            {
                foreach (string k in new[] { r.TitleKey, r.DescKey })
                {
                    string t = GameText.Get(k, GameLanguage.ZhCn) + " " + GameText.Get(k, GameLanguage.En);
                    hits.AddRange(banned.Where(w => t.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).Select(w => $"{k}:{w}"));
                }
            }
            string[] nodes = { "node.sky_eyes", "node.guns_up", "node.first_fragment", "node.what_waits" };
            List<string> noMotif = nodes.Where(n => NarrativeCatalog.Find(n) == null || !motifs.Any(m => GameText.Get(NarrativeCatalog.Find(n).BodyKey, GameLanguage.ZhCn).Contains(m))).ToList();
            Expect(texts > 20 && hits.Count == 0 && noMotif.Count == 0,
                $"A5 伏笔分寸（FGR-NAR-004）：第一幕剧情文本 {texts} 处（中英、含演出字幕）与节奏点文本都没有揭示时才说的词（命中 {string.Join(",", hits)}）；" +
                $"四个伏笔节点都带母题（天空 / 轨道 / 返航 / 清点 / 等待，缺 {string.Join(",", noMotif)}）");
        }

        // ───────────────────────────── B 节奏台账 ─────────────────────────────

        private static void CheckPacingFlow()
        {
            CampaignState s = NewWorld(42);
            BuildCatalog.TryGet("belt_t1", out BuildEntry belt);
            bool wake = Act1PacingService.IsReached(s, "wake") && Act1PacingService.Find(s, "wake").Tick == 0;
            bool locked = !BuildCatalog.IsUnlocked(s, belt);
            string describe = ResearchGate.Describe(s, belt.UnlockRule);
            bool noGuide = CampaignObjectiveTracker.FindQuest(s, "guide.belt") == null;
            // 修复轮（第 1 轮审查 P2：原来这里是恒真断言）：图鉴进度在本段的临时文件里（干净的起点）——“第一幕节奏”是讲节奏台账本身的条目，按它的提示“新游戏开局时解锁”。
            // （“传送带”条目还挂着端口连通等钩子，开局家园端口连上时就可能解锁，不拿来做开局负向。）
            bool pacingCodex = MechanicCodex.IsUnlocked("codex.act1.pacing");
            Expect(wake && locked && describe.Contains(GameText.Get("pace.belts_open.title")) && noGuide && pacingCodex,
                $"B1 新游戏：0:00 记下“苏醒”（第 0 步）{wake}；传送带 T1 锁定 {locked}（“{describe}”）；传送带指引还没出现 {noGuide}；图鉴“第一幕节奏”开局解锁 {pacingCodex}");

            // 修好发电机 / 造出第一台履带机（存档事实，按 pace.check_seconds 检查）。
            BuildingRecord gen = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            if (gen != null)
            {
                gen.ConstructionState = BuildingConstructionState.Operational;
            }
            int machine = Spawn(s, HomeValleyLayout.RegionId, new Vector2(4f, 4f));
            Touched.Clear();
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool power = Act1PacingService.IsReached(s, "power");
            bool firstMachine = Act1PacingService.IsReached(s, "first_machine");
            Expect(gen != null && power && firstMachine && Touched.Contains(GuidanceHooks.Act1PacePower) && Touched.Contains(GuidanceHooks.Act1PaceFirstMachine)
                   && MechanicCodex.IsUnlocked("codex.logistics.power") && MechanicCodex.IsUnlocked("codex.economy.assembly"),
                $"B2 修好发电机 → “恢复电力” {power}；第一台战斗履带机 → “第一台新机器” {firstMachine}；各发一次引导钩子、解锁对应图鉴");

            // 修复轮（第 1 轮审查 P1“传送带节奏门槛能被研究绕过”）负向：远征前就把 T2 / 地下 / 分流合流都研究完（开局技术数据够），整族传送带仍锁定、节奏点不提前到达。
            BuildCatalog.TryGet("belt_t2", out BuildEntry belt2);
            BuildCatalog.TryGet("splitter", out BuildEntry splitter);
            BuildCatalog.TryGet("underground_t1", out BuildEntry under1);
            string[] beltNodes = { "logistics.splitter", "logistics.underground", "logistics.belt_t2", "logistics.belt_t3" };
            s.Research.CompletedNodes = (s.Research.CompletedNodes ?? Array.Empty<string>()).Concat(beltNodes).Distinct().ToArray();
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            string[] family = { "belt_t1", "belt_t2", "belt_t3", "splitter", "merger", "underground_t1", "underground_t2", "underground_t3" };
            List<string> leaked = family.Where(id => BuildCatalog.TryGet(id, out BuildEntry e) && BuildCatalog.IsUnlocked(s, e)).ToList();
            string why2 = ResearchGate.Describe(s, belt2.UnlockRule);
            Expect(leaked.Count == 0 && !Act1PacingService.IsReached(s, "belts_open") && belt2.UnlockRule == "research:logistics.belt_t2&pace:belts_open"
                   && splitter.UnlockRule == "research:logistics.splitter&pace:belts_open" && under1.UnlockRule == "research:logistics.underground&pace:belts_open"
                   && why2.Contains(GameText.Get("research.node.logistics.belt_t2")) && why2.Contains(GameText.Get("pace.belts_open.title")),
                $"B2b 负向：远征前研究完传送带 T2 / T3、地下传送带、分流合流——整族 {family.Length} 个条目仍锁定（漏开 {string.Join(",", leaked)}），节奏点没提前到；" +
                $"T2 的规则 {belt2.UnlockRule}，说明“{why2}”");

            // 第一次远征：进入破碎都市（远征中传送带仍锁定）→ 回到家园才开放。锚点先解析（正式出发时检查单 / 出发事务会解析；撤离后从锚点旁走回家园）。
            ExpeditionSiteAnchors.EnsureResolved(s);
            FracturedCityController city = EnterCity(s, machine);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool entered = Act1PacingService.IsReached(s, "first_expedition");
            bool stillLocked = !Act1PacingService.IsReached(s, "belts_open") && !BuildCatalog.IsUnlocked(s, belt);
            Expect(city != null && city.IsLoaded && entered && stillLocked && MechanicCodex.IsUnlocked("codex.world.site_content"),
                $"B3 远征队进入破碎都市 → “第一次远征”{entered}、图鉴“远征地点：领地的一角”解锁；还在地点里时传送带仍锁定（负向）{stillLocked}");

            int opens0 = NotifyCount(Act1PacingService.NotifyOpen);
            // 修复轮（第 1 轮审查 P1）：走正式的撤离 → 返程 → 到家结算（ExpeditionReturnService 撤离事务里的同一串调用：离开地点、BeginReturnFromSite；
            // 到家用 ReturnNowForTests 放到家门口后走正式的 ReturnHome），不再用 SetMarch 夹具直接清掉行军状态。
            city.Exit(true);
            ExpeditionMarchService.BeginReturnFromSite(s, City, new[] { machine });
            string afterBegin = $"{ExpeditionMarchService.Current(s)?.Phase}/{ExpeditionMarchService.StatusOf(s)}/成员 {ExpeditionMarchService.Current(s)?.Members?.Length}/区域 {Rec(machine)?.RegionId}";
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool lockedOnWay = ExpeditionMarchService.IsMarching(s) && !Act1PacingService.IsReached(s, "belts_open");
            string onWay = $"{ExpeditionMarchService.Current(s)?.Phase}/{ExpeditionMarchService.StatusOf(s)}";
            bool settled = ExpeditionMarchService.ReturnNowForTests(s);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            Expect(lockedOnWay && settled, $"B3b 负向：撤离后远征队还在返程路上时传送带仍锁 {lockedOnWay}（开始返程 {afterBegin}；2 秒后 {onWay}）；到家走正式的到家结算（ReturnHome）{settled}");
            bool beltsOpen = Act1PacingService.IsReached(s, "belts_open") && BuildCatalog.IsUnlocked(s, belt);
            bool guide = CampaignObjectiveTracker.FindQuest(s, "guide.belt") != null;
            bool familyOpen = family.All(id => BuildCatalog.TryGet(id, out BuildEntry e) && BuildCatalog.IsUnlocked(s, e));
            Expect(beltsOpen && NotifyCount(Act1PacingService.NotifyOpen) == opens0 + 1 && guide && MechanicCodex.IsUnlocked("codex.logistics.belt")
                   && ResearchGate.Describe(s, belt.UnlockRule) == GameText.Format("pace.gate.done", GameText.Get("pace.belts_open.title")) && familyOpen,
                $"B4 远征队回到家园 → “基础传送带开放”{beltsOpen}：一条“新系统”通知、图鉴“传送带”、传送带指引出现 {guide}；已研究的 T2 / 地下 / 分流合流随之可建 {familyOpen}");

        }

        private static void CheckHookAndLeaderPoints()
        {
            CampaignState s = NewWorld(1);
            long t0 = GameClock.Ticks;
            GuidanceHooks.Raise(GuidanceHooks.TurretFirstMode); // 无关钩子
            bool none = !Act1PacingService.IsReached(s, "first_turret");
            WorldSimulation.StepMany(5);
            GuidanceHooks.Raise(GuidanceHooks.TurretFirstBuilt);
            ActPaceRecord turret = Act1PacingService.Find(s, "first_turret");
            long at = turret?.Tick ?? -1;
            WorldSimulation.StepMany(5);
            GuidanceHooks.Raise(GuidanceHooks.TurretFirstBuilt);
            int records = s.Progress.Pacing.Count(r => r.Id == "first_turret");
            Expect(none && turret != null && at == GameClock.Ticks - 5 && records == 1 && Act1PacingService.Find(s, "first_turret").Tick == at,
                $"B5 事件型节奏点：无关钩子不记（负向）{none}；“建成炮塔”钩子当场记到（第 {at} 步）；再触发不重记、时刻不变（记录 {records} 条）");

            TerritoryControlService.MarkLeaderDefeated(s, "silent");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool silent = Act1PacingService.IsReached(s, "silent_mind");
            bool act1EndEarly = Act1PacingService.IsReached(s, "act1_end");
            TerritoryControlService.MarkLeaderDefeated(s, "foundry");
            NarrativeService.Trigger(s, "leader:foundry");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool foundry = Act1PacingService.IsReached(s, "foundry_core");
            bool end = Act1PacingService.IsReached(s, "act1_end") && s.Progress.Act == 2;
            Expect(silent && !act1EndEarly && foundry && end && t0 >= 0,
                $"B6 击败寂听主脑 → “击败寂听主脑”{silent}（第一幕还没结束 {!act1EndEarly}）；击败铸造主核心 + 收到“它在等什么” → “击败铸造主核心”{foundry}、“第一幕结束”（第 2 幕）{end}");
        }

        private static void CheckSkipOptional()
        {
            // 卡片负向：玩家跳过接入、炮塔、突袭、静默夜、超控阵列——主线照样一路走到第一幕结束。
            CampaignState s = NewWorld(20261017);
            BuildingRecord gen = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            if (gen != null)
            {
                gen.ConstructionState = BuildingConstructionState.Operational;
            }
            int m = Spawn(s, HomeValleyLayout.RegionId, new Vector2(4f, 4f));
            FracturedCityController city = EnterCity(s, m);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            city.Exit(true);
            SetMarch(s, Array.Empty<int>(), ExpeditionMarchService.PhaseNone);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord fr = FoundryOutpostRegion.Find(s);
            fr.State = RegionState.Available;
            fr.ExpeditionCount = 1;
            MachineRecord rec = Rec(m);
            MachineRegistry.MoveToRegion(rec, Fo);
            FoundryOutpostController fo = WorldSimulation.LoadFoundryOutpost(new[] { m }, resume: false);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            fo.Exit(true);
            TerritoryControlService.MarkLeaderDefeated(s, "silent");
            TerritoryControlService.MarkLeaderDefeated(s, "foundry");
            NarrativeService.Trigger(s, "leader:foundry");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            string[] mainline = Act1PacingService.AllRows().Where(r => r.Mainline == 1).Select(r => r.Id).ToArray();
            string[] optional = { "first_uplink", "first_turret", "first_raid", "first_silent_night", "override_t1" };
            bool allMain = mainline.All(id => Act1PacingService.IsReached(s, id));
            bool noneOptional = optional.All(id => !Act1PacingService.IsReached(s, id));
            var lines = new List<(string Text, string Cls)>();
            Act1PacingService.BuildStatsLines(s, lines);
            int skippedLines = lines.Count(l => l.Text.Contains(GameText.Get("pace.stats.row_skipped").Split('{')[0].Trim()) || l.Text.Contains("可跳过"));
            Expect(allMain && noneOptional && s.Progress.Act == 2 && skippedLines >= optional.Length,
                $"B7 负向“跳过某个系统不影响主线推进”：从不接入、不建炮塔、没遇突袭 / 静默夜、不建超控阵列——主线 {mainline.Length} 个节奏点全部到达 {allMain}、可选的一个都没到 {noneOptional}、进入第 2 幕；统计面板把它们写成“可跳过”（{skippedLines} 行）");
        }

        private static void CheckPauseAndSpeed()
        {
            var ticks = new List<long>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewWorld(7);
                WorldSimulation.StepMany(GameClock.StepHz / 2 + 3); // 落在检查周期中间
                BuildingRecord gen = s.BuildingRecords.First(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
                gen.ConstructionState = BuildingConstructionState.Operational;
                // 暂停：时钟不走、节奏点不记。
                GameClock.SetPaused(true);
                long before = GameClock.Ticks;
                for (int i = 0; i < 30; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool pausedHeld = GameClock.Ticks == before && !Act1PacingService.IsReached(s, "power");
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                for (int i = 0; i < 400 && !Act1PacingService.IsReached(s, "power"); i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                GameClock.SetSpeed(1f);
                ticks.Add(pausedHeld ? Act1PacingService.Find(s, "power")?.Tick ?? -1 : -2);
            }
            Expect(ticks.All(t => t > 0) && ticks.Distinct().Count() == 1,
                $"B8 暂停时节奏台账不推进；0.5x / 1x / 2x / 3x 下“恢复电力”都记在同一步（只看步序号）：{string.Join(" / ", ticks)}");
        }

        private static void CheckPacingSaveLoadAndMigration()
        {
            CampaignState s = NewWorld(42);
            BuildingRecord gen = s.BuildingRecords.First(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            gen.ConstructionState = BuildingConstructionState.Operational;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink);
            string saved = PaceFingerprint(s);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = ReloadHome(Slot);
            string loaded = PaceFingerprint(back);
            BuildCatalog.TryGet("belt_t1", out BuildEntry belt);
            Expect(sv.Success && back != null && loaded == saved && loaded.Contains("first_uplink") && !BuildCatalog.IsUnlocked(back, belt),
                $"B9 存读档：节奏台账逐字段一致（{loaded}），读档不补发、不重记；传送带仍锁定");

            // 旧档（节奏台账上线前）：已经放过传送带、修好发电机，统一时钟走过 → 载入时按事实补记（Migrated、不通知），传送带照常可用。
            CampaignState old = NewWorldState(5);
            old.Progress.Pacing = Array.Empty<ActPaceRecord>();
            old.Clock.Ticks = GameClock.StepHz * 3600L;
            old.Belts ??= new BeltItemState();
            old.Belts.CellCount = 12;
            BuildingRecord ogen = old.BuildingRecords?.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            if (ogen != null)
            {
                ogen.ConstructionState = BuildingConstructionState.Operational;
            }
            int notes0 = NotifyCount(Act1PacingService.NotifyOpen);
            Touched.Clear();
            Act1PacingService.OnCampaignEntered(old);
            bool migrated = Act1PacingService.Find(old, "belts_open")?.Migrated == true && (ogen == null || Act1PacingService.Find(old, "power")?.Migrated == true)
                            && Act1PacingService.Find(old, "wake") != null;
            Expect(migrated && BuildCatalog.IsUnlocked(old, belt) && NotifyCount(Act1PacingService.NotifyOpen) == notes0 && !Touched.Any(h => h.StartsWith("act1.pace.", StringComparison.Ordinal)),
                $"B10 旧档迁移：按事实补记（传送带 / 电力 / 苏醒，Migrated）{migrated}；研发前放下的传送带照常可用；不发通知、不发节奏钩子");

            CampaignState fresh = NewWorld(3);
            bool lockedFresh = !BuildCatalog.IsUnlocked(fresh, belt);
            Act1PacingService.LegacyGatesOpenForJourneys = true;
            bool journey = BuildCatalog.IsUnlocked(fresh, belt);
            Act1PacingService.LegacyGatesOpenForJourneys = false;
            ResearchGate.LegacyGatesOpenForTests = true;
            bool legacy = BuildCatalog.IsUnlocked(fresh, belt);
            ResearchGate.LegacyGatesOpenForTests = false;
            Expect(lockedFresh && journey && legacy && JourneyBotHost.PrePacingJourney("FGJ-M8") && JourneyBotHost.PrePacingJourney("FGJ-M3R")
                   && !JourneyBotHost.PrePacingJourney("FGJ-GEN9") && !JourneyBotHost.PrePacingJourney("FGJ-M9"),
                "B11 旧规则开关：M0～M8 旅程与全量自检的旧段按开放前的规则（传送带可建），FGJ-M9 / GEN9（复用 FGJ-M9 步骤，DEBT-FG3GEN01-10）起按正式节奏；真实游戏两个开关都关");

            var lines = new List<(string Text, string Cls)>();
            Act1PacingService.BuildStatsLines(s, lines);
            Expect(lines.Count == 15 && lines[0].Cls == "st-row-title" && lines.Any(l => l.Text.Contains(GameText.Get("pace.power.title")) && l.Cls == "st-row-warn"),
                $"B12 统计面板“第一幕节奏”：标题 + 14 行；“恢复电力”在第 0:00 就到了（目标 0:10），偏离超过 25% 标黄：{string.Join(" | ", lines.Take(3).Select(l => l.Text))}");
        }

        // ───────────────────────────── C 远征地点的生成内容 ─────────────────────────────

        private static void CheckSiteLayoutSeeds()
        {
            var fingerprints = new List<string>();
            foreach (int seed in Seeds)
            {
                CampaignState s = NewWorld(seed);
                foreach (string site in new[] { City, Fo })
                {
                    SiteGenContent c = SiteGenLayout.For(s, site);
                    if (c == null)
                    {
                        Fail($"C1 种子 {seed} {site}：没有生成内容");
                        continue;
                    }
                    bool outside = c.Garrisons.All(g => !c.InDemoRect(g.Local, c.Row.Margin) && c.InWindow(g.Local, 2f) && !SiteGenLayout.InObstacle(c, g.Local, 0.5f));
                    bool demoClear = c.Obstacles.All(o => !c.InDemoRect(new Vector2(o.x, o.y), 0f)) && c.SlagZones.All(z => !c.InDemoRect(new Vector2(z.x, z.y), z.z));
                    // 地形障碍 = 星球真实地形（按锚点朝向变换后的格不可通行）。
                    WorldTerrainSource src = WorldGenService.ContextFor(s).Source;
                    // 修复轮（第 1 轮审查 P1）：每个障碍圆的圆心都是星球真实地形里不可通行的格（全部核对，不再只抽查 12 个）；
                    // 窗口里全部不可通行格都被障碍圆盖住（一格不丢，不截断），障碍数不超上限。
                    bool real = true;
                    foreach (Vector3 o in c.Obstacles)
                    {
                        TerritoryPlanner.ToWorld(c.Anchor, o.x, o.y, out int wx, out int wy);
                        src.Sample(wx, wy, out byte terrain, out _);
                        real &= GridContent.TerrainByCode(terrain)?.NavCost == 0;
                    }
                    bool coveredAll = c.BlockedSamples > 0 && c.CoveredBlocked == c.BlockedSamples && string.IsNullOrEmpty(c.Problem);
                    // 铸造前哨：驻军（连同围着它的一圈单位）与熔渣热区都在核心门封锁线以下；封锁线以上只有 Demo 核心区，外圈的禁区里没有障碍。
                    bool below = site != Fo || (c.Garrisons.All(g => g.Local.y <= FoundryOutpostLayout.CoreGateBlockLineY - 6f)
                                                && c.SlagZones.All(z => z.y + z.z <= FoundryOutpostLayout.CoreGateBlockLineY)
                                                && c.Obstacles.All(o => !c.InOuterBand(new Vector2(o.x, o.y))));
                    Expect(c.Garrisons.Count == c.Row.Garrisons && outside && demoClear && real && coveredAll && below && c.Obstacles.Count <= c.Row.ObstacleMax && c.ReachableSamples > 0
                           && (site != Fo || c.SlagZones.Count == c.Row.SlagZones),
                        $"C1 种子 {seed} {site}：{c.Garrisons.Count} 处驻军都在 Demo 矩形外 {c.Row.Margin} 米、与入口连通（按放好的障碍圆重算）、不压地形障碍{(site == Fo ? "、都在核心门封锁线以下" : "")}；" +
                        $"不可通行格 {c.BlockedSamples} 个全部被 {c.Obstacles.Count} 个障碍圆盖住（{c.CoveredBlocked}/{c.BlockedSamples}，合并档位 {c.MergeLevel}，上限 {c.Row.ObstacleMax}），圆心全部取自星球真实地形；" +
                        $"Demo 矩形里没有生成内容；熔渣热区 {c.SlagZones.Count}；可走取样 {c.ReachableSamples}/{c.WalkableSamples}{(string.IsNullOrEmpty(c.Problem) ? "" : "；问题：" + c.Problem)}");
                    fingerprints.Add(string.Join(";", c.Garrisons.Select(g => $"{g.Local.x},{g.Local.y}")));
                    SiteGenContent again = SiteGenLayout.For(s, site);
                    if (!ReferenceEquals(again, c))
                    {
                        Fail("C1 同一存档第二次取生成内容没有走缓存");
                    }
                }
            }
            Expect(fingerprints.Distinct().Count() == fingerprints.Count,
                "C1b B25：驻军位置随种子变化（3 个种子 × 2 个地点的驻军布局互不相同，不依赖固定坐标）");
        }

        private static void CheckGarrisonRegistration()
        {
            CampaignState s = NewWorld(42);
            List<OutpostRecord> sites = WorldOutpostSystem.Outposts(s).Where(o => o.Kind == WorldOutpostSystem.KindSite).ToList();
            int silentTotal;
            TerritoryControlService.ComputeControl(s, "silent", out _, out silentTotal);
            int silentSiteWeight = sites.Where(o => o.TerritoryId == "silent").Sum(o => Math.Max(1, o.Tier));
            int territoryOnly = WorldOutpostSystem.Outposts(s).Where(o => o.Kind == WorldOutpostSystem.KindTerritory && o.TerritoryId == "silent").Sum(o => Math.Max(1, o.Tier));
            int again = SiteGarrisonService.SeedAll(s);
            Expect(sites.Count == 7 && sites.All(o => o.OutpostId.StartsWith(SiteGarrisonService.Prefix, StringComparison.Ordinal) && o.Dormant && !o.Destroyed)
                   && silentTotal == territoryOnly + silentSiteWeight && again == 0 && s.World.SiteGarrisonsSeeded,
                $"C2 新战役登记远征地点驻军据点 {sites.Count} 处（kind = site，从第 0 步起休眠）；静默领地控制度分母 = 领地据点 {territoryOnly} + 地点驻军 {silentSiteWeight} = {silentTotal}（DEBT-FG8EXP03-06）；再登记幂等");

            // 星球上不迎战展开：机器站在驻军据点的星球格上，迎战检查跑过几轮，驻军据点不展开。
            OutpostRecord one = sites.First(o => o.TerritoryId == "silent");
            int planetMachine = Spawn(s, HomeValleyLayout.RegionId, new Vector2(one.CellX, one.CellY));
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool notEngaged = !one.Engaged && one.Garrison > 0 && planetMachine > 0;
            // 不作突袭出发地：静默的其它据点全部摧毁、只剩地点驻军，静默突袭从迷雾外来。
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s).Where(o => o.Kind != WorldOutpostSystem.KindSite).ToList())
            {
                WorldOutpostSystem.MarkDestroyed(s, o.OutpostId);
            }
            RaidDirectorService.RequestStoryRaid(s, "silent", 1, "fgact1-origin");
            for (int i = 0; i < 40 && RaidDirectorService.Plans(s).Count == 0; i++)
            {
                WorldSimulation.StepMany(GameClock.StepHz);
            }
            RaidPlanRecord plan = RaidDirectorService.Plans(s).FirstOrDefault();
            Expect(notEngaged && plan != null && (plan.OriginId == null || !plan.OriginId.StartsWith(SiteGarrisonService.Prefix, StringComparison.Ordinal)),
                $"C3 负向：机器站在驻军据点的星球格上也不在星球上迎战展开 {notEngaged}；只剩地点驻军时突袭不从驻军据点出发（出发地 {plan?.OriginKind}:{plan?.OriginId}）");
        }

        private static void CheckGarrisonFight()
        {
            CampaignState s = NewWorld(42);
            int m = Spawn(s, HomeValleyLayout.RegionId, new Vector2(3f, 3f));
            FracturedCityController city = EnterCity(s, m);
            WorldSimulation.StepMany(3);
            SiteGenContent c = city.SiteGen;
            List<RegionEnemyRecord> garrison = s.RegionEnemies.Where(e => e.RegionId == City && SiteGarrisonService.IsGarrisonInstance(e.EnemyInstanceId)).ToList();
            List<string> demo = s.RegionEnemies.Where(e => e.RegionId == City && !SiteGarrisonService.IsGarrisonInstance(e.EnemyInstanceId)).Select(e => e.EnemyInstanceId).OrderBy(x => x).ToList();
            List<string> demoWant = EnemyDefs.Encounters(City, EnemyDefs.ConditionBase).Select(e => e.Id).OrderBy(x => x).ToList();
            bool inKernel = garrison.All(e => city.Combat.TryGetEnemyUnit(e.EnemyInstanceId, out _));
            int expect = c.Garrisons.Sum(g => Mathf.Clamp(WorldOutpostSystem.Find(s, g.OutpostId).Garrison, 1, c.Row.UnitsMax));
            int demoObstacles = FracturedCityLayout.AllAnchors().Count();
            Expect(c != null && garrison.Count == expect && inKernel && demo.SequenceEqual(demoWant) && c.Obstacles.Count > 0,
                $"C4 进入破碎都市：领地驻军按当前驻军播种 {garrison.Count} 台（应 {expect}）并进战斗内核；Demo 编成原样（{demo.Count} 个，{string.Join(",", demo)}）；" +
                $"外圈 {c?.Obstacles.Count} 个地形障碍 + Demo 锚点净空 {demoObstacles} 个");

            // 部分击杀不摧毁；全灭 = 摧毁。
            SiteGarrisonPoint g0 = c.Garrisons[0];
            OutpostRecord o0 = WorldOutpostSystem.Find(s, g0.OutpostId);
            List<RegionEnemyRecord> squad = garrison.Where(e => SiteGarrisonService.OutpostOfInstance(e.EnemyInstanceId) == g0.OutpostId).ToList();
            TerritoryControlService.ComputeControl(s, "silent", out _, out _);
            int controlBefore = TerritoryControlService.ComputeControl(s, "silent", out _, out _);
            FracturedCityRegion.TryDamageEnemy(s, squad[0].EnemyInstanceId, 99999f);
            WorldSimulation.StepMany(2);
            bool partial = !o0.Destroyed && TerritoryControlService.ComputeControl(s, "silent", out _, out _) == controlBefore;
            int alerts0 = NotifyCount(TerritoryControlService.NotifyAlert);
            foreach (RegionEnemyRecord e in squad.Skip(1))
            {
                FracturedCityRegion.TryDamageEnemy(s, e.EnemyInstanceId, 99999f);
            }
            WorldSimulation.StepMany(2);
            int controlAfter = TerritoryControlService.ComputeControl(s, "silent", out _, out _);
            GroundItemRecord scrap = s.GroundItems.FirstOrDefault(gi => gi.RegionId == City && gi.SalvageInstanceId != null && gi.SalvageInstanceId.Contains(g0.OutpostId));
            Expect(partial && o0.Destroyed && controlAfter < controlBefore && NotifyCount(TerritoryControlService.NotifyAlert) > alerts0 && scrap != null
                   && (scrap.Position - g0.Local).sqrMagnitude < 1f && Touched.Contains(GuidanceHooks.SiteFirstGarrisonDestroyed),
                $"C5 负向：打掉一台不算摧毁 {partial}；这一处 {squad.Count} 台全灭 → 据点被摧毁、静默控制度 {controlBefore}% → {controlAfter}%、领地通知、按等级的废料掉在地点里驻军处（{scrap?.Amount}）");

            // 远征队走近自动拾取（DEBT-FG8EXP05-01）。
            SetMarch(s, new[] { m }, ExpeditionMarchService.PhaseAtSite);
            Place(city.Combat, m, g0.Local + new Vector2(1f, 0f));
            int before = scrap.Amount;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool picked = !s.GroundItems.Contains(scrap) || scrap.Amount < before;
            Expect(picked, $"C6 远征地点里普通战利品走近自动拾取（与星球同一规则）：驻军掉落的废料被队员装进货舱 {picked}");
        }

        private static void CheckGarrisonRebuildAndSave()
        {
            CampaignState s = NewWorld(1);
            int m = Spawn(s, HomeValleyLayout.RegionId, new Vector2(3f, 3f));
            FracturedCityController city = EnterCity(s, m);
            WorldSimulation.StepMany(3);
            SiteGenContent c = city.SiteGen;
            SiteGarrisonPoint g0 = c.Garrisons[0];
            int seeded = s.RegionEnemies.Count(e => SiteGarrisonService.IsGarrisonInstance(e.EnemyInstanceId));
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = ReloadCity(Slot);
            int after = back.RegionEnemies.Count(e => SiteGarrisonService.IsGarrisonInstance(e.EnemyInstanceId));
            bool kernel = back.RegionEnemies.Where(e => SiteGarrisonService.IsGarrisonInstance(e.EnemyInstanceId) && e.IsAlive)
                .All(e => GameRoot.FracturedCity != null && GameRoot.FracturedCity.Combat.TryGetEnemyUnit(e.EnemyInstanceId, out _));
            Expect(sv.Success && back != null && after == seeded && kernel,
                $"C7 地点里存读档：驻军记录随存档恢复（{seeded} → {after}）、不重复播种、全部回到战斗内核");

            // 摧毁一处 → 地点载入时增援不重建（玩家在场）；卸载后重建 → 下次进场换一组新实例。
            OutpostRecord o0 = WorldOutpostSystem.Find(back, g0.OutpostId);
            foreach (RegionEnemyRecord e in back.RegionEnemies.Where(e => SiteGarrisonService.OutpostOfInstance(e.EnemyInstanceId) == g0.OutpostId).ToList())
            {
                FracturedCityRegion.TryDamageEnemy(back, e.EnemyInstanceId, 99999f);
            }
            WorldSimulation.StepMany(2);
            bool destroyed = o0.Destroyed;
            bool busySkip = SiteGarrisonService.SiteBusy(City);
            GameRoot.FracturedCity.Exit(true);
            bool rebuilt = WorldOutpostSystem.Rebuild(back, o0.OutpostId, 3) && !o0.Destroyed && o0.DestroyCount == 1;
            MachineRecord rec = Rec(m) ?? back.MachineRecords.FirstOrDefault(r => r.IsAlive);
            MachineRegistry.MoveToRegion(rec, City);
            WorldSimulation.LoadFracturedCity(new[] { rec.LogicId }, resume: false);
            WorldSimulation.StepMany(2);
            int fresh = back.RegionEnemies.Count(e => e.EnemyInstanceId.StartsWith(g0.OutpostId + ".d1.", StringComparison.Ordinal) && e.IsAlive);
            Expect(destroyed && busySkip && rebuilt && fresh == Math.Min(3, c.Row.UnitsMax),
                $"C8 驻军被摧毁 {destroyed}；地点载入时视为玩家在场（增援不重建）{busySkip}；卸载后被重建 → 再进场播种新一轮实例（.d1.，{fresh} 台），旧的阵亡记录留作墓碑");
        }

        private static void CheckFoundrySite()
        {
            CampaignState s = NewWorld(42);
            int m = Spawn(s, HomeValleyLayout.RegionId, new Vector2(3f, 3f));
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord region = FoundryOutpostRegion.Find(s);
            region.State = RegionState.Available;
            region.ExpeditionCount = 1;
            MachineRegistry.MoveToRegion(Rec(m), Fo);
            Rec(m).WorldPosition = FoundryOutpostLayout.EntryEvac.Position;
            FoundryOutpostController fo = WorldSimulation.LoadFoundryOutpost(new[] { m }, resume: false);
            WorldSimulation.StepMany(3);
            SiteGenContent c = fo.SiteGen;
            bool slag = c != null && c.SlagZones.Count > 0 && fo.Combat.Kernel.SlagZoneCount == c.SlagZones.Count;
            // 驻军里的步进炮（关键掉落 = 铸造重炮模块）被打掉不掉关键物：关键物只属于 Demo 手工内容。
            RegionEnemyRecord strider = s.RegionEnemies.FirstOrDefault(e => e.RegionId == Fo && SiteGarrisonService.IsGarrisonInstance(e.EnemyInstanceId) && e.EnemyTypeId == "enemy_strider");
            int quest0 = s.RegionQuestItems?.Length ?? 0;
            if (strider != null)
            {
                FoundryOutpostRegion.TryDamageEnemy(s, strider.EnemyInstanceId, 99999f);
                WorldSimulation.StepMany(2);
            }
            bool noKey = strider != null && (s.RegionQuestItems?.Length ?? 0) == quest0;
            // 信号所在的机器进入地点熔渣热区 → 提示一次。
            int notes0 = NotifyCount("slag_heat");
            Vector3 z = c.SlagZones[0];
            Place(fo.Combat, m, new Vector2(z.x, z.y));
            s.SignalCore.UplinkMachineLogicId = m; // 信号在这台机器里（与接入完成写的是同一个存档字段）
            WorldSimulation.StepMany(GameClock.StepHz * 2 + 1);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool told = NotifyCount("slag_heat") == notes0 + 1 && s.Factions.SiteSlagNoticed.Length == 1;
            Expect(slag && noKey && told && c.Garrisons.Count == c.Row.Garrisons,
                $"C9 铸造前哨：外圈 {c?.SlagZones.Count} 片熔渣热区进地点战斗内核（{fo.Combat.Kernel.SlagZoneCount}，都在 Demo 矩形外，不改 Demo 主核心战）；信号机器进入热区提示一次 {told}；" +
                $"驻军步进炮被打掉不掉铸造重炮模块（关键物只属于 Demo 手工内容）{noKey}");
        }

        // ───────────────── 修复轮（第 1 轮审查 P1）：驻军真交战可达、铸造前哨核心门的禁区、前中期远征要不要先铺中继 ─────────────────

        private static void CheckGarrisonReachByCommands()
        {
            var report = new List<string>();
            bool ok = true;
            var sw = Stopwatch.StartNew();
            foreach (int seed in Seeds)
            {
                foreach (string site in new[] { City, Fo })
                {
                    CampaignState s = NewWorld(seed);
                    int[] team = { Spawn(s, HomeValleyLayout.RegionId, new Vector2(3f, 3f)), Spawn(s, HomeValleyLayout.RegionId, new Vector2(5f, 3f)), Spawn(s, HomeValleyLayout.RegionId, new Vector2(7f, 3f)) };
                    CombatSite combat;
                    RegionSquadCommandSystem cmds;
                    SiteGenContent c;
                    RegionRecord foRegion = null;
                    if (site == City)
                    {
                        FracturedCityController city = EnterCityTeam(s, team);
                        combat = city.Combat;
                        cmds = city.SquadCommands;
                        c = city.SiteGen;
                    }
                    else
                    {
                        FoundryOutpostController fo = EnterFoundryTeam(s, team);
                        combat = fo.Combat;
                        cmds = fo.SquadCommands;
                        c = fo.SiteGen;
                        foRegion = FoundryOutpostRegion.Find(s);
                    }
                    WorldSimulation.StepMany(3);
                    int control0 = TerritoryControlService.ComputeControl(s, c.TerritoryId, out _, out _);
                    Vector2 entry = SiteGenLayout.EntryLocal(site);
                    int destroyed = 0;
                    int issued = 0;
                    int refused = 0;
                    int slowest = 0;
                    bool crossed = false;
                    var missed = new List<string>();
                    foreach (SiteGarrisonPoint g in c.Garrisons.OrderBy(g => (g.Local - entry).sqrMagnitude).ToList())
                    {
                        OutpostRecord o = WorldOutpostSystem.Find(s, g.OutpostId);
                        int sec = 0;
                        for (; sec < 240 && o != null && !o.Destroyed; sec++)
                        {
                            bool attacking = team.Any(id => cmds.TryGetActiveCommandKind(id, out RegionCommandKind k) && k == RegionCommandKind.Attack);
                            if (!attacking)
                            {
                                // 像玩家一样：选中三台，右键这处驻军里离队伍最近的一台（目标死了命令结束，不会自己找别的敌人，再右键下一台）。
                                string target = NearestGarrisonUnit(s, combat, g, team);
                                if (target == null)
                                {
                                    break;
                                }
                                cmds.DebugSelectMany(team);
                                cmds.IssueAttack(target, false);
                                issued++;
                                if (!team.Any(id => cmds.TryGetActiveCommandKind(id, out RegionCommandKind k2) && k2 == RegionCommandKind.Attack))
                                {
                                    refused++;
                                }
                            }
                            WorldSimulation.StepMany(GameClock.StepHz);
                            crossed |= site == Fo && combat.AnyMachineBeyondY(FoundryOutpostLayout.CoreGateBlockLineY);
                        }
                        slowest = Math.Max(slowest, sec);
                        if (o != null && o.Destroyed)
                        {
                            destroyed++;
                        }
                        else
                        {
                            missed.Add($"{g.OutpostId}@({g.Local.x:F0},{g.Local.y:F0}) {sec} 秒");
                        }
                    }
                    int control1 = TerritoryControlService.ComputeControl(s, c.TerritoryId, out _, out _);
                    bool noBoss = site != Fo || (foRegion != null && !FoundryOutpostCoreBoss.IsInitialized(foRegion) && !foRegion.CoreGateUnlocked);
                    bool pass = destroyed == c.Garrisons.Count && control1 < control0 && refused == 0 && !crossed && noBoss && combat.CountAliveMachines() > 0;
                    ok &= pass;
                    report.Add($"种子 {seed} {site}：{destroyed}/{c.Garrisons.Count} 处打光（最慢一处 {slowest} 游戏秒，右键攻击 {issued} 次、被拒 {refused}），控制度 {control0}% → {control1}%" +
                               (site == Fo ? $"，门锁着、没有机器越过核心门封锁线 {!crossed}、首领战没开始 {noBoss}" : string.Empty) +
                               (missed.Count > 0 ? "；没打下：" + string.Join("、", missed) : string.Empty));
                }
            }
            Expect(ok, $"C11 驻军可达（第 1 轮审查 P1，{sw.Elapsed.TotalSeconds:F0} 秒）：3 个种子 × 两个地点，三台机器从入口出发，只用正式的“右键敌人 = 攻击”命令" +
                       $"（编队命令系统 → 战斗内核的直线前进 + 局部避障，真交战，不直接扣血、不传送）逐处打光领地驻军：" + string.Join("；", report));
        }

        private static string NearestGarrisonUnit(CampaignState s, CombatSite combat, SiteGarrisonPoint g, int[] team)
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
            Vector2 from = n > 0 ? sum / n : g.Local;
            string best = null;
            float bestD = float.MaxValue;
            foreach (RegionEnemyRecord e in s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (e == null || !e.IsAlive || SiteGarrisonService.OutpostOfInstance(e.EnemyInstanceId) != g.OutpostId || !combat.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 at))
                {
                    continue;
                }
                float d = (at - from).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = e.EnemyInstanceId;
                }
            }
            return best;
        }

        private static void CheckFoundryCoreBand()
        {
            CampaignState s = NewWorld(42);
            int m = Spawn(s, HomeValleyLayout.RegionId, new Vector2(3f, 3f));
            FoundryOutpostController fo = EnterFoundryTeam(s, new[] { m });
            WorldSimulation.StepMany(3);
            SiteGenContent c = fo.SiteGen;
            float line = FoundryOutpostLayout.CoreGateBlockLineY;
            Vector2 side = c.ClampToPlayable(new Vector2(50f, 40f));
            Vector2 high = c.ClampToPlayable(new Vector2(60f, 90f));
            Vector2 core = c.ClampToPlayable(new Vector2(10f, 40f));
            Vector2 outer = c.ClampToPlayable(new Vector2(40f, 10f));
            Vector2 far = c.ClampToPlayable(new Vector2(500f, -500f));
            bool clamps = c.OuterMaxY == line && c.Playable(side) && c.Playable(high) && core == new Vector2(10f, 40f) && outer == new Vector2(40f, 10f) && c.Playable(far)
                          && !c.Playable(new Vector2(50f, 40f)) && (side.y <= line || c.InDemoRect(side, 0f)) && (high.y <= line || c.InDemoRect(high, 0f));
            Expect(clamps, $"C12 铸造前哨的外圈上沿 = 核心门封锁线 {line}：核心区两侧 / 上方是禁区（(50,40) → {side}、(60,90) → {high}），核心区里 / 外圈的点不动，窗口外夹进窗口（{far}）");

            // 门锁着：正式的右键地面 = 移动，点核心区一侧的禁区——目的地被夹到封锁线以下，机器不越线、首领战不开始。
            RegionRecord region = FoundryOutpostRegion.Find(s);
            fo.SquadCommands.DebugSelectMany(new[] { m });
            fo.SquadCommands.IssueMoveTo(new Vector2(45f, 0f), false);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            fo.SquadCommands.IssueMoveTo(new Vector2(50f, 40f), false);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            fo.Combat.TryGetMachinePosition(m, out Vector2 at);
            bool stayed = !region.CoreGateUnlocked && !fo.Combat.AnyMachineBeyondY(line) && at.y <= line + 0.5f && !FoundryOutpostCoreBoss.IsInitialized(region) && at.x > c.Row.DemoHalfX;
            Expect(stayed, $"C13 负向：门锁着时在外圈右键核心区一侧的禁区（50,40）——机器停在 ({at.x:F1},{at.y:F1})（封锁线 {line} 以下、核心区宽度 {c.Row.DemoHalfX} 之外），首领战没开始");
        }

        private static void CheckEarlyRelayEvaluation()
        {
            // DEBT-FG8EXP02-02 / DEBT-FG1SIG07-01 的评估（结论写 ADR-NAR-002 第 7 节；切不切由用户定口径，承接 FG9-BAL-01）：
            // 默认模式 1（接入点恒连通）下，第一次去两个地点都不用先铺中继（C11：地点里每处驻军的命令都没被覆盖拒绝）；
            // 模式 2（锚点要在家园覆盖里）下，按每个种子的锚点距离算出前中期要先铺几座中继塔。
            int mode = SignalCoverageService.SiteRootMode;
            var lines = new List<string>();
            bool rootOk = true;
            foreach (int seed in Seeds)
            {
                CampaignState s = NewWorld(seed);
                ExpeditionSiteAnchors.EnsureResolved(s); // 锚点在第一次需要时解析（检查单 / 出发）；评估这里先解析
                foreach (string site in new[] { City, Fo })
                {
                    bool connected = SignalCoverageService.SiteRootConnected(s, site);
                    rootOk &= connected;
                    if (ExpeditionSiteAnchors.TryGet(s, site, out Vector2 anchor))
                    {
                        float dist = (anchor - HomeValleyLayout.Core.Position).magnitude;
                        int relays = Mathf.Max(0, Mathf.CeilToInt((dist - SignalCoverageService.TowerRadius) / SignalCoverageService.RelayTowerRadius));
                        lines.Add($"种子 {seed} {site} 锚点离核心 {dist:F0} 格（模式 2 要先铺 {relays} 座中继塔）");
                    }
                }
            }
            Expect(mode == 1 && rootOk && lines.Count == Seeds.Length * 2,
                $"C14 覆盖根评估（DEBT-FG8EXP02-02）：默认模式 {mode}（接入点恒连通），第一次远征两个地点都不用先铺中继 {rootOk}；" +
                $"若改成模式 2（信号塔半径 {SignalCoverageService.TowerRadius:F0}、中继塔半径 {SignalCoverageService.RelayTowerRadius:F0}）：{string.Join("；", lines)}");
        }

        private static FracturedCityController EnterCityTeam(CampaignState s, int[] team)
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

        private static FoundryOutpostController EnterFoundryTeam(CampaignState s, int[] team)
        {
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord region = FoundryOutpostRegion.Find(s);
            region.State = RegionState.Available;
            region.ExpeditionCount = Math.Max(1, region.ExpeditionCount);
            for (int i = 0; i < team.Length; i++)
            {
                MachineRecord rec = Rec(team[i]);
                MachineRegistry.MoveToRegion(rec, Fo);
                rec.WorldPosition = FoundryOutpostLayout.EntryEvac.Position + new Vector2((i % 3 - 1) * 2.5f, -1f);
            }
            FoundryOutpostController fo = WorldSimulation.LoadFoundryOutpost(team, resume: false);
            SetMarch(s, team, ExpeditionMarchService.PhaseAtSite);
            return fo;
        }

        private static void CheckOldWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            SiteGenLayout.ResetCache();
            CampaignState s = CampaignState.CreateNew("fgact1-v2", "Standard", 9);
            WorldGenService.ApplyNewGameWorld(s, 9, WorldSettings.Resolve(2, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            WorldSimulation.LoadHome(resume: false);
            int m = Spawn(s, HomeValleyLayout.RegionId, new Vector2(3f, 3f));
            FracturedCityController city = EnterCity(s, m);
            WorldSimulation.StepMany(3);
            bool demoOnly = city.SiteGen == null && !s.RegionEnemies.Any(e => SiteGarrisonService.IsGarrisonInstance(e.EnemyInstanceId))
                            && !WorldOutpostSystem.Outposts(s).Any(o => o.Kind == WorldOutpostSystem.KindSite) && SignalContactService.Decoys(s).Count == 0;
            Expect(demoOnly, "C10 负向：旧世界（生成器 v2，没有锚点）——远征地点保持 Demo 原样：没有生成内容、没有驻军据点、没有诱饵中继");
        }

        // ───────────────────────────── D 诱饵中继假信号点 ─────────────────────────────

        private static void CheckDecoyContacts()
        {
            CampaignState s = NewWorld(42);
            IReadOnlyList<(string Id, Vector2 Cell)> decoys = SignalContactService.Decoys(s).ToList();
            WorldPlan plan = WorldGenService.PlanFor(s);
            PlannedTerritory silent = plan.Find("silent");
            bool placed = decoys.Count == 4 && decoys.All(d => (d.Cell - new Vector2(silent.CenterX, silent.CenterY)).magnitude <= silent.Radius
                && !TerritoryPlanner.NearAnyAnchor(plan, Mathf.RoundToInt(d.Cell.x), Mathf.RoundToInt(d.Cell.y), 8));
            CampaignState other = NewWorld(1);
            bool seeded = !SignalContactService.Decoys(other).Select(d => d.Cell).SequenceEqual(decoys.Select(d => d.Cell));
            s = NewWorld(42);
            decoys = SignalContactService.Decoys(s).ToList();
            Expect(placed && seeded, $"D1 诱饵中继 {decoys.Count} 个按种子放在静默领地里、离锚点平整范围 8 格以外；换种子位置不同（B25）");

            // 没有在外的队伍：不侦测（负向）。
            WorldSimulation.StepMany(GameClock.StepHz * 4);
            bool none = s.Factions.ContactsDetected.Length == 0;
            // 远征队员站到诱饵旁：侦测到附近的信号点（诱饵 + 真据点一样）。
            (string did, Vector2 dc) = decoys[0];
            // 队员站在侦测半径里、但离诱饵足够远（机器自己的视野会揭开周围迷雾，揭开了就不是“迷雾里的信号”了）。
            int m = Spawn(s, HomeValleyLayout.RegionId, dc + new Vector2(110f, 0f));
            SetMarch(s, new[] { m }, ExpeditionMarchService.PhaseOutbound);
            int notes0 = NotifyCount(SignalContactService.NotifyType);
            WorldSimulation.StepMany(GameClock.StepHz * 4);
            bool detected = SignalContactService.IsDetected(s, did) && NotifyCount(SignalContactService.NotifyType) > notes0 && Touched.Contains(GuidanceHooks.SiteFirstContactSeen);
            // 再放一个真据点进侦测范围（静默领地据点），地图上二者同一样式同一标签。
            OutpostRecord real = WorldOutpostSystem.Outposts(s).Where(o => o.TerritoryId == "silent" && TerritoryControlService.CountsForControl(o) && !o.Destroyed)
                .OrderBy(o => (new Vector2(o.CellX, o.CellY) - dc).sqrMagnitude).First();
            SignalContactService.Detect(s, Vector2.Distance(new Vector2(real.CellX, real.CellY), dc) + 20f);
            var model = new WorldMapModel();
            WorldMapFilters.ResetAll();
            model.Collect(s, ViewAround(dc, Vector2.Distance(new Vector2(real.CellX, real.CellY), dc) + 60f), 400);
            WorldMapItem? decoyItem = Item(model, "contact:" + did);
            WorldMapItem? realItem = Item(model, "contact:" + real.OutpostId);
            string unknown = GameText.Get("site.contact.unknown");
            Expect(none && detected && decoyItem != null && realItem != null && decoyItem.Value.Label == unknown && realItem.Value.Label == unknown
                   && decoyItem.Value.Done == realItem.Value.Done,
                $"D2 没有在外的队伍不侦测（负向）{none}；队员靠近 → 侦测到信号点并通知 {detected}；地图上诱饵与真据点同一样式、同一标签“{decoyItem?.Label}”/“{realItem?.Label}”（真假看不出）");

            // 甄别：监听站破译出一条反制预览 → 顺带甄别；再来一条不重复通知。
            int notes1 = NotifyCount(SignalContactService.NotifyType);
            IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            int notes2 = NotifyCount(SignalContactService.NotifyType);
            IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            int notes3 = NotifyCount(SignalContactService.NotifyType);
            model.Collect(s, ViewAround(dc, Vector2.Distance(new Vector2(real.CellX, real.CellY), dc) + 60f), 400);
            WorldMapItem? decoyAfter = Item(model, "contact:" + did);
            WorldMapItem? realAfter = Item(model, "contact:" + real.OutpostId);
            Expect(SignalContactService.IsIdentified(s, did) && notes2 == notes1 + 1 && notes3 == notes2 && decoyAfter?.Label == GameText.Get("site.contact.decoy")
                   && realAfter?.Label == GameText.Get("site.contact.real") && decoyAfter.Value.Done && !realAfter.Value.Done,
                $"D3 监听站“反制预览”情报顺带甄别：诱饵写“{decoyAfter?.Label}”、真据点写“{realAfter?.Label}”；第二条反制预览不再重复通知（{notes3 - notes2}）");

            // 揭开诱饵所在的迷雾 → 写“已识破”；存读档保留侦测 / 甄别。
            HomeGridService.RevealArea(s, dc, 12f);
            model.Collect(s, ViewAround(dc, 80f), 400);
            WorldMapItem? seen = Item(model, "contact:" + did);
            string[] det = s.Factions.ContactsDetected.ToArray();
            string[] ide = s.Factions.ContactsIdentified.ToArray();
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = ReloadHome(Slot);
            Expect(seen?.Label == GameText.Get("site.contact.decoy_seen") && sv.Success && back != null && back.Factions.ContactsDetected.SequenceEqual(det) && back.Factions.ContactsIdentified.SequenceEqual(ide),
                $"D4 揭开迷雾的诱饵写“{seen?.Label}”；侦测（{det.Length}）/ 甄别（{ide.Length}）随存档恢复");
        }

        // ───────────────────────────── E 第二幕 / 静默夜估时 ─────────────────────────────

        private static void CheckActTwoAndSilentEstimate()
        {
            CampaignState s = NewWorld(42);
            CampaignObjectiveTracker.Recompute(s);
            bool clarityBefore = CampaignObjectiveTracker.FindQuest(s, "main.key.clarity") == null;
            IntelCatalog.TryGet(IntelCatalog.KindFleet, out IntelKindDef fleet);
            bool fleetBefore = !IntelService.Needed(s, fleet, GameClock.Ticks, out _);
            TerritoryControlService.MarkLeaderDefeated(s, "foundry");
            NarrativeService.Trigger(s, "leader:foundry");
            CampaignObjectiveTracker.Recompute(s);
            bool clarityAfter = CampaignObjectiveTracker.FindQuest(s, "main.key.clarity") != null;
            bool fleetAfter = IntelService.Needed(s, fleet, GameClock.Ticks, out _);
            Expect(clarityBefore && fleetBefore && s.Progress.Act == 2 && clarityAfter && fleetAfter,
                $"E1 收到“它在等什么”→ 第 2 幕：第二幕领地（澄净）的主线任务出现 {clarityAfter}（之前没有 {clarityBefore}）；监听站的舰队片段情报从“不需要”变成“需要”{fleetAfter}");

            // 派遣检查单的静默夜提醒按路线估时：路线短的队伍估时 < 固定 12 小时。
            ExpeditionSiteAnchors.EnsureResolved(s);
            int m = Spawn(s, HomeValleyLayout.RegionId, new Vector2(3f, 3f));
            double est = SilentNightService.ExpeditionEstimateSeconds(s, City, new[] { m });
            double fixedEst = SilentNightService.ExpeditionEstimateHours * GameClock.HourSeconds;
            float route = ExpeditionSiteAnchors.RouteLength(s, City);
            double want = route > 0 ? 2.0 * route / ExpeditionMarchService.MarchSpeed(new[] { m }) + 2.0 * GameClock.HourSeconds : fixedEst;
            double noRoster = SilentNightService.ExpeditionEstimateSeconds(s, City, Array.Empty<int>());
            Expect(Math.Abs(est - want) < 1.0 && (route <= 0 || est < fixedEst) && Math.Abs(noRoster - fixedEst) < 1e-6,
                $"E2 DEBT-FG7ENV02-03：远征估时 = 往返路线（{route:0} 格 ÷ 行军速度）+ 地点停留 2 小时 = {est / GameClock.HourSeconds:0.00} 小时（固定估计 {fixedEst / GameClock.HourSeconds:0} 小时只在路线 / 队伍未知时兜底）");
        }

        // ───────────────────────────── F 性能 ─────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorld(20261017);
            SiteGenLayout.ResetCache();
            var sw = Stopwatch.StartNew();
            SiteGenLayout.For(s, City);
            double cityMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            SiteGenLayout.For(s, Fo);
            double foMs = sw.Elapsed.TotalMilliseconds;
            s = NewWorld(20261017);
            int m = Spawn(s, HomeValleyLayout.RegionId, new Vector2(3f, 3f));
            SetMarch(s, new[] { m }, ExpeditionMarchService.PhaseOutbound);
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                SignalContactService.Detect(s, 150f);
                Act1PacingService.Evaluate(s, GameClock.Ticks, quiet: true);
            }
            double stepUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 200.0;
            Line($"  - 性能（Editor batchmode，一次测量；真机门禁在 FG15-SYS-02）：生成内容 破碎都市 {cityMs:0.0} ms / 铸造前哨 {foMs:0.0} ms（每个存档每个地点一次）；信号侦测 + 节奏检查 {stepUs:0.0} µs / 次");
            Expect(cityMs < 60.0 && foMs < 60.0 && stepUs < 200.0,
                $"F1 性能预算：生成内容每个地点 < 60 ms（实测 {cityMs:0.0} / {foMs:0.0}）；信号侦测 + 节奏检查每次 < 200 µs（实测 {stepUs:0.0}，各自每 1～2 游戏秒才跑一次）");

            // 修复轮（障碍圆上限 120 → 160）：破碎都市里三台机器正在和驻军交战时，整个世界一步（家园 + 地点战斗内核的避障 / 视线遍历全部障碍）的耗时。
            CampaignState s3 = NewWorld(42);
            int[] team = { Spawn(s3, HomeValleyLayout.RegionId, new Vector2(3f, 3f)), Spawn(s3, HomeValleyLayout.RegionId, new Vector2(5f, 3f)), Spawn(s3, HomeValleyLayout.RegionId, new Vector2(7f, 3f)) };
            FracturedCityController city = EnterCityTeam(s3, team);
            WorldSimulation.StepMany(3);
            SiteGarrisonPoint g0 = city.SiteGen.Garrisons.OrderBy(g => (g.Local - SiteGenLayout.EntryLocal(City)).sqrMagnitude).First();
            string target = NearestGarrisonUnit(s3, city.Combat, g0, team);
            city.SquadCommands.DebugSelectMany(team);
            if (target != null)
            {
                city.SquadCommands.IssueAttack(target, false);
            }
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            sw.Restart();
            const int steps = 600;
            WorldSimulation.StepMany(steps);
            double worldUs = sw.Elapsed.TotalMilliseconds * 1000.0 / steps;
            Line($"  - 性能：破碎都市交战中（障碍圆 {city.SiteGen.Obstacles.Count} 个、地点敌人 {city.Combat.EnemyIds.Count()} 个）整个世界一步 {worldUs:0.0} µs");
            Expect(worldUs < 2000.0, $"F2 性能预算：地点交战中整个世界一步 < 2000 µs（实测 {worldUs:0.0}，障碍圆 {city.SiteGen.Obstacles.Count} 个；Editor batchmode 一次测量，真机门禁在 FG15-SYS-02）");
        }

        // ───────────────────────────── 工具 ─────────────────────────────

        private static WorldMapView ViewAround(Vector2 c, float half) =>
            new WorldMapView { CenterX = c.x, CenterY = c.y, HalfWidth = half, CanvasWidth = 400f, CanvasHeight = 400f };

        private static WorldMapItem? Item(WorldMapModel model, string id)
        {
            foreach (WorldMapItem it in model.Items)
            {
                if (it.Id == id)
                {
                    return it;
                }
            }
            return null;
        }

        private static string PaceFingerprint(CampaignState s) =>
            string.Join(";", (s?.Progress?.Pacing ?? Array.Empty<ActPaceRecord>()).Select(r => $"{r.Id}@{r.Tick}{(r.Migrated ? "M" : "")}"));

        private static FracturedCityController EnterCity(CampaignState s, int machine)
        {
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            RegionRecord region = FracturedCityRegion.Find(s);
            region.State = RegionState.Available;
            region.ExpeditionCount = Math.Max(1, region.ExpeditionCount); // 出发事务提交时 +1（ExpeditionDepartureService）
            MachineRecord rec = Rec(machine);
            MachineRegistry.MoveToRegion(rec, City);
            rec.WorldPosition = FracturedCityLayout.EntryEvac.Position;
            FracturedCityController city = WorldSimulation.LoadFracturedCity(new[] { machine }, resume: false);
            SetMarch(s, new[] { machine }, ExpeditionMarchService.PhaseAtSite);
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

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site != null && site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new Unity.Mathematics.double2(at.x, at.y));
            }
            if (MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
            {
                rec.WorldPosition = at;
            }
        }

        private static int Spawn(CampaignState s, string region, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, region, at, 4000f, 4000f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static CampaignState ReloadHome(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            Act1PacingService.OnCampaignEntered(r.State);
            return r.State;
        }

        private static CampaignState ReloadCity(int fromSlot)
        {
            CampaignState st = ReloadHome(fromSlot);
            if (st == null)
            {
                return null;
            }
            int[] there = st.MachineRecords.Where(m => m.RegionId == City && m.IsAlive).Select(m => m.LogicId).ToArray();
            WorldSimulation.LoadFracturedCity(there, resume: true);
            WorldSimulation.StepMany(2);
            return st;
        }

        private static CampaignState NewWorldState(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            SiteGenLayout.ResetCache();
            CampaignState s = CampaignState.CreateNew("fgact1-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            return s;
        }

        private static CampaignState NewWorld(int seed)
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
            CampaignState s = CampaignState.CreateNew("fgact1-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            Act1PacingService.OnCampaignEntered(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(e => e.Type != null && e.Type.Id == typeId).Sum(e => e.Members.Count);

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgAct1SelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
                ResearchGate.LegacyGatesOpenForTests = false;
                Act1PacingService.LegacyGatesOpenForJourneys = false;
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
