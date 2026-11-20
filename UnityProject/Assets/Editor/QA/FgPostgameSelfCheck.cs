using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
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
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.HomeValleyFailure;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG14-SBX-01 后日谈与周期清洗（FG12 FGR-SBX-001～005，验收 FGT-SBX-001～003）自检：起真实世界（家园内核、突袭导演、行进、攻城、结算、存档）跑——
    /// A 数据（7 日周期、突袭等级、后日谈专属遗产组）；B 结算点“继续”→ 起算坚守、说明、第 7 / 14 日两次清洗按时到来、预算 1200 × 1.15ⁿ、等级递增、打完记统计与历史、存档卡、坚守记录面板、顶栏；
    /// C 负向：后日谈里的普通突袭不占清洗序号、清洗没建出计划 → 不算来过、同一序号重发；D 高等级：同时存在的敌人（含登陆舱投放）≤ 200、全部精英后单位强度按预算增长、真实攻城展开数与耐久倍率；
    /// E 暂停与 0.5x～3x；F 存读档；G 观察一致；H 坚守结束（核心被毁 → 总结页、读档继续、之后不再来清洗）；I 轨道坠落更频繁、后日谈专属遗产只由坠落带来。
    /// </summary>
    public static class FgPostgameSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string FailureUxml = "Assets/GameRes/Raw/UI/HomeValleyFailure/HomeValleyFailure.uxml";
        private const string PanelUxml = "Assets/GameRes/Raw/UI/UiKit/PostgamePanel.uxml";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _introEvents;

        [MenuItem("QA/自检/FG 后日谈")]
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
            Line("\n[后日谈] 结局后继续、每 7 个游戏日一次周期清洗（预算 1200 × 1.15ⁿ、同时存在 ≤ 200、精英化与单位强度）、坚守统计与总结页、轨道坠落与最后一组遗产（FG14-SBX-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgpostgame-selfcheck-" + Guid.NewGuid().ToString("N"));
            GameEvent.AddEventListener(PostgameService.IntroEvent, OnIntro);
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
                PostgameService.ResetCountersForTests();

                Step(CheckData);
                Step(CheckStartCadenceAndStats);
                Step(CheckNormalRaidAndRetry);
                Step(CheckHighLevel);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckStandOver);
                Step(CheckOrbitalAndLegacy);
            }
            finally
            {
                GameEvent.RemoveEventListener(PostgameService.IntroEvent, OnIntro);
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
            Line(_fail == 0 ? $"  后日谈：全部 {_pass} 项通过" : $"  后日谈：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        private static void OnIntro() => _introEvents++;

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            var pg = HeritageService.All.Where(HeritageService.PostgameOnly).ToList();
            bool tuning = Math.Abs(PostgameService.PeriodDays - 7.0) < 1e-6 && PostgameService.RaidLevel == 4 && PostgameService.OrbitalDropRate > 1f
                          && PostgameService.OrbitalLegacyChance > 0f && Math.Abs(RaidCatalog.PostgameBase - 1200f) < 1e-3f && Math.Abs(RaidCatalog.PostgameGrowth - 1.15f) < 1e-4f
                          && RaidCatalog.MaxUnits == 200;
            bool group = pg.Count >= 3 && pg.All(h => h.Story == 0 && h.Category != HeritageService.CatFirmware && !GameText.ContainsMarker(HeritageService.Name(h)));
            Expect(tuning && group,
                $"A1 数据：清洗周期 {PostgameService.PeriodDays} 个游戏日（FGR-SBX-002 初值 7）、突袭等级 {PostgameService.RaidLevel}、预算 {RaidCatalog.PostgameBase} × {RaidCatalog.PostgameGrowth}ⁿ、上限 {RaidCatalog.MaxUnits}；" +
                $"后日谈专属遗产 {pg.Count} 件（{string.Join("、", pg.Select(HeritageService.Name))}）");
        }

        // ─────────────────────────────── B 开始、周期、统计 ───────────────────────────────

        private static void CheckStartCadenceAndStats()
        {
            CampaignState s = NewPostgame(14011, out long start);
            PostgameState st = PostgameService.StateOf(s);
            long due0 = st.NextPurgeTick;
            bool started = st.StartTick == start && due0 == start + RaidDirectorService.DayTicks(7) && _introEvents == 1 && Notified("postgame.notify.start", out _)
                           && GameSettings.HasSeenGuidanceHook(GuidanceHooks.PostgameFirstStart) && s.Progress.IsPostgame && !EndingService.SettlementOpen;
            Expect(started,
                $"B1 结算点“继续”→ 进入后日谈：第 {st.StartTick} 步起算坚守、第一次清洗排在 7 个游戏日后（第 {due0} 步）、说明面板事件 {_introEvents} 次、“进入后日谈”通知与引导钩子");

            GameClock.SkipForTests(s, due0 - 2 * GameClock.StepHz - GameClock.Ticks);
            WorldSimulation.StepMany(GameClock.StepHz);
            bool notEarly = st.Launched == 0 && !RaidDirectorService.Plans(s).Any(p => p != null && p.StoryId.StartsWith(PostgameService.PurgeIdPrefix, StringComparison.Ordinal));
            RaidPlanRecord p0 = LaunchAndPlan(s, 0);
            if (p0 == null)
            {
                return;
            }
            float scale = DifficultyService.Scale(s);
            bool first = notEarly && st.History[0].LaunchTick == due0 && p0.PostgameIndex == 0 && p0.Trigger == RaidCatalog.TriggerPurge && p0.TargetKind == RaidDirectorService.TargetHome
                         && p0.Budget == (int)Math.Round(1200.0 * scale) && p0.Faction == EnemyDefs.FactionFleet && Notified("postgame.notify.purge", out string n0);
            Expect(first,
                $"B2 第 7 日之前不来（{notEarly}）；第 {st.History[0].LaunchTick} 步（= 第 {due0} 步）交给突袭导演：触发 purge、白潮、打家园、清洗序号 {p0.PostgameIndex}、预算 {p0.Budget}（= 1200 × 难度规模 {scale}）");
            if (!RunToCleared(s, p0, 1, out int killed1))
            {
                return;
            }
            PostgamePurgeRecord r0 = st.History[0];
            int resKilled = RaidResultService.All(s).Where(r => r != null && r.PlanId == p0.PlanId).Sum(r => r.Killed);
            bool noteCleared = Notified("postgame.notify.cleared", out string c0);
            Expect(st.Cleared == 1 && r0.Outcome == PostgameService.OutcomeRepelled && r0.Killed == resKilled && r0.Killed > 0 && r0.Units == p0.Occupancy && r0.Units <= 200
                   && noteCleared,
                $"B3 第 1 级清洗全歼 → 记撑过：击毁 {r0.Killed}（= 突袭结算 {resKilled}）、损失 {r0.Losses}、同时存在 {r0.Units} 台；通知“{c0}”");
            var purgeRows = new List<LootDropDef>();
            LootCatalog.RowsFor(LootCatalog.SourcePurge, EnemyDefs.FactionFleet, purgeRows);
            var res0 = RaidResultService.All(s).Where(r => r != null && r.PlanId == p0.PlanId).ToList();
            long Got(string item) => res0.Sum(r => (r.Loot ?? Array.Empty<ItemAmountRecord>()).Where(a => a != null && a.ItemId == item).Sum(a => a.Amount));
            int piles = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Count(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix + LootCatalog.SourcePurge + ":", StringComparison.Ordinal));
            string lootText = string.Join("；", res0.Select(RaidResultService.LootText));
            bool salvage = purgeRows.Count > 0 && purgeRows.All(row => Got(row.Item.Id) > 0) && res0.Any(r => r.Loot.Any(a => a.ItemId == AnalysisCatalog.WreckId))
                           && purgeRows.All(row => lootText.Contains(ItemCatalog.NameOf(row.Item.Id))) && !GameText.ContainsMarker(lootText);
            Expect(salvage,
                $"B3b 舰队残骸回收（FGR-SBX-002“得到人类预制固件和稀有材料”）：白潮单位残骸照常回收，另按 purge 行掉稀有材料（{string.Join("、", purgeRows.Select(r => $"{r.Item.Id}×{Got(r.Item.Id)}"))}，" +
                $"落地 {piles} 堆还没搬走），结算“缴获”写出：{lootText}（预制固件身份见 FgFleetFactionSelfCheck H1）");

            long due1 = due0 + RaidDirectorService.DayTicks(7);
            bool cadence = st.NextPurgeTick == due1;
            RaidPlanRecord p1 = LaunchAndPlan(s, 1);
            if (p1 == null)
            {
                return;
            }
            bool second = cadence && st.History[1].LaunchTick == due1 && p1.PostgameIndex == 1 && p1.Budget == (int)Math.Round(1200.0 * 1.15 * scale) && p1.Budget > p0.Budget
                          && st.HighestLevel == 2 && PostgameService.NextLevel(s) == 2;
            Expect(second,
                $"B4 第 14 日（第 {st.History[1].LaunchTick} 步 = 上一次排定 + 7 日 {due1}）第 2 级清洗：序号 {p1.PostgameIndex}、预算 {p0.Budget} → {p1.Budget}（× 1.15）、最高等级 {st.HighestLevel}");
            if (!RunToCleared(s, p1, 2, out int killed2))
            {
                return;
            }
            int days = PostgameService.HoldDays(s, GameClock.Ticks);
            int bestKilled = Math.Max(st.History[0].Killed, st.History[1].Killed);
            bool stats = st.Cleared == 2 && st.HighestCleared == 2 && days == (int)((GameClock.Ticks - start) / RaidDirectorService.DayTicks(1)) && days >= 14
                         && st.BestKilled == bestKilled && st.TotalKilled == st.History[0].Killed + st.History[1].Killed
                         && st.TotalLosses == st.History[0].Losses + st.History[1].Losses
                         && PostgameService.SummaryLine(s, GameClock.Ticks).Contains($"坚守 {days} 天") && PostgameService.BestLine(s).Contains($"击毁 {bestKilled}");
            Expect(stats,
                $"B5 坚守统计（FGR-SBX-003）：{PostgameService.SummaryLine(s, GameClock.Ticks)}；{PostgameService.BestLine(s)}；{PostgameService.TotalsLine(s)}");

            SaveResult sr = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(Slot);
            string card = CampaignSlotText.FgFields(meta);
            Expect(sr.Success && meta.IsPostgame && meta.PostgameDays == days && meta.PostgameTopLevel == 2 && card.Contains($"坚守 {days} 天") && card.Contains("最高 2 级"),
                $"B6 存档卡片显示坚守天数与最高等级（FGR-SBX-003 / 卡片“存档卡片显示坚守天数”）：“{card}”");

            string hud = PostgameService.HudText(s, GameClock.Ticks);
            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PanelUxml).CloneTree();
            var go = new GameObject("__FgPostgamePanel") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                PostgamePanelUIToolkit.InWorldOverrideForTests = true;
                PostgamePanelUIToolkit panel = go.AddComponent<PostgamePanelUIToolkit>();
                panel.BindView(tree);
                PostgamePanelUIToolkit.Open();
                bool record = PostgamePanelUIToolkit.IsOpen && !PostgamePanelUIToolkit.IntroMode && panel.VisibleRowCount == 2 && panel.RowText(0).Contains("第 2 级")
                              && panel.RowText(1).Contains("第 1 级") && panel.RowText(0).Contains("撑过") && panel.SummaryText.Contains($"坚守 {days} 天")
                              && panel.NextText.Contains("第 3 级") && panel.FooterText.Contains("200") && !GameText.ContainsMarker(panel.SummaryText + panel.BestText + panel.RowText(0) + panel.FooterText);
                string row0 = panel.RowText(0);
                PostgamePanelUIToolkit.OpenIntro();
                string intro = panel.IntroText;
                bool introOk = PostgamePanelUIToolkit.IntroMode && intro.Contains("7 个游戏日") && intro.Contains("200 台") && intro.Contains("读取自动存档") && !GameText.ContainsMarker(intro);
                panel.SetOpen(false, false);
                Expect(record && introOk && hud.Contains("第 3 级清洗"),
                    $"B7 坚守记录面板（清洗历史可以查看，最近的在前）：“{row0}”；{panel.SummaryText}；“说明”重看后日谈说明（{introOk}）；顶栏“{hud}”");
            }
            finally
            {
                PostgamePanelUIToolkit.InWorldOverrideForTests = false;
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
            // 失败页（坚守总结复用）的布局探针在 SettingsConsumersSelfCheck 的面板表里
            string probe = BinGames.EditorTools.UiToolkitLayoutProbe.Probe(PanelUxml, "PostgameWindow", stressFill: true,
                prepare: rr => rr.panel.visualTree.Q<VisualElement>("PostgameRoot")?.RemoveFromClassList("uk-hidden"));
            Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "B8 坚守记录面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
        }

        // ─────────────────────────────── C 负向：普通突袭、重发 ───────────────────────────────

        private static void CheckNormalRaidAndRetry()
        {
            CampaignState s = NewPostgame(14012, out _);
            PostgameState st = PostgameService.StateOf(s);
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            int pc0 = d.PostgameCount;
            _ = d;
            RaidDirectorService.RequestStoryRaid(s, "silent", 3);
            WorldSimulation.StepMany(2);
            RaidPlanRecord story = RaidDirectorService.Plans(s).LastOrDefault(p => p != null && p.Trigger == RaidCatalog.TriggerStory);
            Expect(story != null && story.PostgameIndex == -1 && d.PostgameCount == pc0 && story.Strength == 1f,
                $"C1 负向：后日谈里的普通 / 剧情突袭不占清洗序号（序号 {story?.PostgameIndex}、清洗计数 {pc0} → {d.PostgameCount}），预算照常规曲线（{story?.Budget}）");

            long due0 = st.NextPurgeTick;
            RaidPlanRecord first = LaunchAndPlan(s, 0);
            bool launched = first != null && st.Launched == 1 && st.ActiveIndex == 0 && st.HighestLevel == 1;
            // 测试捷径：把导演建好的这一波记为取消（正式路径里是派不出队伍 / 没有路线等，导演写同一个结束原因）。
            if (first != null)
            {
                first.State = RaidDirectorService.StateCancelled;
                first.EndReason = RaidDirectorService.EndCancelled;
            }
            int retries0 = PostgameService.Retries;
            WorldSimulation.StepMany(GameClock.StepHz * 2 + 1);
            bool retried = PostgameService.Retries == retries0 + 1 && st.Launched == 0 && st.ActiveIndex < 0 && st.History.Length == 0;
            int highWait = st.HighestLevel;
            long beatWait = st.NextPurgeTick, retryAt = st.RetryTick;
            bool waitOk = highWait == 0 && retryAt > GameClock.Ticks && PostgameService.NextLaunchTick(st) == retryAt && beatWait == due0 + RaidDirectorService.DayTicks(7);
            RaidPlanRecord again = F.StepUntil(() => st.ActivePlanId.Length > 0, 90) ? RaidDirectorService.FindPlan(s, st.ActivePlanId) : null;
            bool beat = st.RetryTick < 0 && st.NextPurgeTick == due0 + RaidDirectorService.DayTicks(7);
            Expect(launched && retried && waitOk && beat && again != null && again != first && again.PostgameIndex == 0 && st.Launched == 1 && st.HighestLevel == 1,
                $"C2 负向：清洗没建出计划 → 不算来过（重发 {PostgameService.Retries - retries0} 次、已来 {st.Launched}、历史 {st.History.Length} 行、最高等级退回 {highWait}），" +
                $"60 游戏秒后用同一个序号重发（序号 {again?.PostgameIndex}，不跳档）；重发等待时倒计时指向重发时刻 {retryAt}，下一次节拍不顺延（{st.NextPurgeTick} = 排定 {due0} + 7 日）");
        }

        // ─────────────────────────────── D 高等级：上限、精英化、单位强度 ───────────────────────────────

        private static void CheckHighLevel()
        {
            CampaignState s = NewPostgame(14013, out _);
            var rows = new List<string>();
            bool capOk = true;
            float prevStrength = 1f;
            bool grows = true;
            RaidPlanRecord p30 = null;
            foreach (int n in new[] { 0, 10, 20, 30, 40 })
            {
                var p = new RaidPlanRecord { Serial = 900 + n, Faction = EnemyDefs.FactionFleet, Level = 4, PostgameIndex = n, Trigger = RaidCatalog.TriggerPurge };
                p.Budget = RaidDirectorService.ComputeBudget(s, RaidDirectorService.StateOf(s), p);
                RaidDirectorService.Compose(s, p);
                int elites = p.EliteCounts.Sum();
                bool allElite = p.UnitCounts.Sum() == elites;
                capOk &= p.Occupancy <= RaidCatalog.MaxUnits && p.UnitTotal <= RaidCatalog.MaxUnits && Array.IndexOf(p.UnitIds, "fleet.drop_pod") >= 0;
                if (n >= 30)
                {
                    grows &= allElite && p.Strength > prevStrength && p.Occupancy == RaidCatalog.MaxUnits;
                }
                if (n == 0)
                {
                    grows &= p.Strength == 1f;
                }
                prevStrength = n >= 30 ? p.Strength : prevStrength;
                if (n == 30)
                {
                    p30 = p;
                }
                rows.Add($"第 {n + 1} 级 预算 {p.Budget}：{p.UnitTotal} 台（同时存在 {p.Occupancy}、精英 {elites}）强度 ×{p.Strength:F2}");
            }
            Expect(capOk && grows,
                "D1 高等级清洗（FGT-SBX-002 / FG12 第 4 节）：同时存在的敌人（含登陆舱投放）始终 ≤ 200，超出预算先换精英、全部精英后单位强度按预算继续增长（等级没有上限）：" + string.Join("；", rows));
            var hi = new List<RaidPlanRecord>();
            foreach (int n in new[] { 100, 103, 120, 200 })
            {
                // 同一个计划序号（编成抽取的种子）：强度 = 预算 ÷ 已花点数，换种子时编成不同、强度会跟着补偿（总战力 = 预算），只有同一编成下强度才随预算单调
                var p = new RaidPlanRecord { Serial = 999, Faction = EnemyDefs.FactionFleet, Level = 4, PostgameIndex = n, Trigger = RaidCatalog.TriggerPurge };
                p.Budget = RaidDirectorService.ComputeBudget(s, RaidDirectorService.StateOf(s), p);
                RaidDirectorService.Compose(s, p);
                hi.Add(p);
            }
            bool mono = hi[0].Budget > 0 && hi.Zip(hi.Skip(1), (a, b) => b.Budget >= a.Budget && b.Strength >= a.Strength).All(x => x)
                        && hi.All(p => p.Occupancy <= RaidCatalog.MaxUnits && p.Occupancy == RaidCatalog.MaxUnits && p.Strength >= prevStrength)
                        && hi[3].Budget == int.MaxValue;
            Expect(mono,
                "D1b 等级没有上限、预算越过 int 不塌（审查 P2：约第 104 级起 1200 × 1.15ⁿ 超过 int）：" + string.Join("；", hi.Select(p => $"第 {p.PostgameIndex + 1} 级 预算 {p.Budget}、同时存在 {p.Occupancy}、强度 ×{p.Strength:F0}")) +
                "（预算封顶 int.MaxValue 后强度随之停涨，见 ADR-SBX-001 待复核）");

            // 真实攻城：第 31 级清洗从轨道来到家园，登陆舱落地后同一队伍的单位 ≤ 200，耐久 = 基础 × 精英 × 难度 × 强度。
            PostgameState st = PostgameService.StateOf(s);
            st.Launched = 30; // 测试捷径：已坚守 30 次（统计口径由 B 段覆盖）
            RaidPlanRecord p31 = LaunchAndPlan(s, 30);
            if (p31 == null)
            {
                return;
            }
            if (!Arrive(s, p31, out int key))
            {
                return;
            }
            CombatSite site = Site;
            int podIdx = Array.IndexOf(p31.UnitIds, "fleet.drop_pod");
            int pods = podIdx >= 0 ? p31.UnitCounts[podIdx] : 0;
            int cargo0 = CombatSite.PodCargoSpawned;
            int peak = 0;
            double kt0 = site.Kernel.Time;
            List<int> arrived = GroupUnits(key);
            var podUnits = arrived.Where(x => site.Kernel.TryGetFleet(x, out CombatFleet f) && f.State == CombatFleetConst.StatePodFalling).ToList();
            int falling0 = podUnits.Count;
            int others = arrived.Count - falling0;
            // 测试捷径：第 31 级清洗（强度 ×13）几秒就能拆掉新世界的核心（核心被毁家园冻结，舱就不落地了）——先清掉随舱一起到的机动单位，只看舱落地投放；
            // 同时存在的最多台数 = 到达时的机动单位 + 舱 + 投放的（都按真实生成数，不按编成推算）。
            TransitGroupRecord g = WorldTransitSystem.Find(s, p31.GroupId);
            float str = SiegeService.StrengthOf(s, g);
            // 到达展开（SiegeService.Deploy → SpawnUnits）生成的机动单位：与“同一 def / 精英标记、只乘难度”的参照单位比，耐久与武器伤害都是 × 强度
            Vector2 refAt = new Vector2(HomeValleyLayout.Core.Position.x + 30f, HomeValleyLayout.Core.Position.y);
            var refs = new List<(float Hp, float Dmg)>();
            foreach (string uid in p31.UnitIds.Distinct())
            {
                RaidUnitDef def = RaidCatalog.Units.First(u => u.Id == uid);
                if (uid == "fleet.drop_pod")
                {
                    continue;
                }
                refs.Add(Ref(site, key, refAt, def, true, s));
            }
            // 登陆舱落地投放（CombatSite.Fleet 投放路径）生成的单位：舱的载荷 def、不是精英，与只乘难度的参照比 × 强度（参照在等落地之前生成并删掉，不混进投放的单位）
            RaidUnitDef podDef = RaidCatalog.Units.First(u => u.Id == "fleet.drop_pod");
            RaidUnitDef cargoDef = EnemyDefs.TryGet(podDef.EnemyTypeId, out GameConfig.fg.MechEnemy podRow) && EnemyFleetParams.Cargo(podRow, out GameConfig.fg.MechEnemy cargoRow, out _)
                ? RaidCatalog.Units.FirstOrDefault(u => u.EnemyTypeId == cargoRow.Id) : null;
            (float Hp, float Dmg) cargoRef = cargoDef != null ? Ref(site, key, refAt, cargoDef, false, s) : (0f, 0f);
            int deployChecked = 0, deployOk = 0;
            foreach (int x in arrived.Except(podUnits))
            {
                if (site.Kernel.TryGetUnit(x, out CombatUnitView v) && site.Kernel.TryGetWeapon(v.Weapon, out CombatWeapon w))
                {
                    deployChecked++;
                    deployOk += refs.Any(r => Math.Abs(v.MaxHealth / (r.Hp * str) - 1f) < 1e-3f && Math.Abs(w.Damage / (r.Dmg * str) - 1f) < 1e-3f) ? 1 : 0;
                }
            }
            foreach (int x in arrived.Except(podUnits))
            {
                site.Kernel.Kill(x, 0);
            }
            // 等到全部登陆舱落地投放完（按投放计数，不按“舱不在下落”——舱可能还没进入下落阶段），期间记同一队伍同时存在的最多台数。
            bool landed = F.StepUntil(() => CombatSite.PodCargoSpawned - cargo0 >= falling0 * 4, 120);
            WorldSimulation.StepMany(GameClock.StepHz);
            peak = others + falling0 + (CombatSite.PodCargoSpawned - cargo0);
            int fallingNow = GroupUnits(key).Count(x => site.Kernel.TryGetFleet(x, out CombatFleet f) && f.State == CombatFleetConst.StatePodFalling);
            string diag = $"（诊断：下落中 {falling0} → {fallingNow}，内核走了 {site.Kernel.Time - kt0:F1} 秒，核心被毁 {HomeValleySoftlockGuard.IsCoreDestroyed(s)}）";
            int cargoChecked = 0, cargoOk = 0;
            foreach (int x in GroupUnits(key).Except(arrived))
            {
                if (site.Kernel.TryGetUnit(x, out CombatUnitView v) && site.Kernel.TryGetWeapon(v.Weapon, out CombatWeapon w))
                {
                    cargoChecked++;
                    cargoOk += cargoRef.Hp > 0f && Math.Abs(v.MaxHealth / (cargoRef.Hp * str) - 1f) < 1e-3f && Math.Abs(w.Damage / (cargoRef.Dmg * str) - 1f) < 1e-3f ? 1 : 0;
                }
            }
            bool hp = deployChecked > 0 && deployOk == deployChecked && cargoChecked > 0 && cargoOk == cargoChecked;
            Expect(landed && pods > 0 && falling0 == pods && peak <= RaidCatalog.MaxUnits && peak == p31.Occupancy && str > 1f && Math.Abs(str - p31.Strength) < 1e-5f && hp,
                $"D2 第 31 级清洗真实攻城：{pods} 个登陆舱落地投放 {CombatSite.PodCargoSpawned - cargo0} 台，同一队伍同时存在最多 {peak} 台（≤ 200，编成 {p31.UnitTotal} 台 / 名额 {p31.Occupancy}）、" +
                $"单位强度 ×{str:F2}（计划 ×{p31.Strength:F2}）；正式路径真实生成的单位耐久与武器伤害 = 基础 × 精英 × 难度 × 强度：到达展开 {deployOk}/{deployChecked}、舱投放 {cargoOk}/{cargoChecked}" + (landed ? string.Empty : diag));
            foreach (int x in GroupUnits(key))
            {
                site.Kernel.Kill(x, 0);
            }
        }

        // ─────────────────────────────── E 暂停与倍速 ───────────────────────────────

        private static void CheckPauseSpeed()
        {
            var rows = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewPostgame(14014, out _);
                PostgameState st = PostgameService.StateOf(s);
                long target = st.NextPurgeTick;
                GameClock.SkipForTests(s, target - 2 * GameClock.StepHz - GameClock.Ticks);
                long t0 = GameClock.Ticks;
                int days0 = PostgameService.HoldDays(s, GameClock.Ticks);
                GameClock.SetPaused(true);
                for (int i = 0; i < 60; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool paused = GameClock.Ticks == t0 && st.Launched == 0 && PostgameService.HoldDays(s, GameClock.Ticks) == days0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                for (int i = 0; i < 20000 && GameClock.Ticks <= target + 1; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                GameClock.SetSpeed(1f);
                bool exact = st.Launched == 1 && st.History.Length == 1 && st.History[0].LaunchTick == target;
                ok &= paused && exact;
                rows.Add($"{speed}x 暂停 6 秒不走（{paused}）、在第 {(st.History.Length > 0 ? st.History[0].LaunchTick : -1)} 步来（应 {target}）");
            }
            Expect(ok, "E1 周期清洗与坚守天数按游戏时间：" + string.Join("；", rows));
        }

        // ─────────────────────────────── F 存读档 ───────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewPostgame(14015, out _);
            PostgameState st = PostgameService.StateOf(s);
            RaidPlanRecord p = LaunchAndPlan(s, 0);
            if (p == null)
            {
                return;
            }
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string sig0 = Sig(s);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            string sigA = Sig(s);
            long t1 = GameClock.Ticks;
            CampaignState l = RestoreSlot(out string fail);
            if (l == null)
            {
                Fail("F1 读档失败：" + fail);
                return;
            }
            string sigL = Sig(l);
            WorldSimulation.StepMany((int)(t1 - GameClock.Ticks));
            string sigB = Sig(l);
            Expect(r.Success && st.ActiveIndex == 0 && GameClock.Ticks == t1 && sigL == sig0 && sigA == sigB && PostgameService.Started(l),
                $"F1 存读档：后日谈标记、清洗计时与等级、进行中的那一次、坚守统计与历史读回 = 存档时（{sigL == sig0}），再走 20 秒与不读档逐项一致（{sigA == sigB}）"
                + (sigA != sigB ? $"\n不读档：{sigA}\n读档后：{sigB}" : string.Empty) + (sigL != sig0 ? $"\n存档时：{sig0}\n读回：{sigL}" : string.Empty));
        }

        // ─────────────────────────────── G 观察一致 ───────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string Run(bool observe)
            {
                CampaignState s = NewPostgame(14016, out _, observe);
                PostgameState st = PostgameService.StateOf(s);
                GameClock.SkipForTests(s, st.NextPurgeTick - 1 - GameClock.Ticks);
                WorldSimulation.StepMany(GameClock.StepHz * 40);
                return Sig(s);
            }
            string a = Run(true);
            string b = Run(false);
            Expect(a == b, $"G1 家园不被观察时后日谈清洗与统计的结果与观察时一致（{a.Length} 字签名）" + (a != b ? $"\n观察：{a}\n不观察：{b}" : string.Empty));
        }

        // ─────────────────────────────── H 坚守结束 ───────────────────────────────

        private static void CheckStandOver()
        {
            CampaignState s = NewPostgame(14017, out long start);
            PostgameState st = PostgameService.StateOf(s);
            RaidPlanRecord p = LaunchAndPlan(s, 0);
            if (p == null)
            {
                return;
            }
            SaveResult saved = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!Arrive(s, p, out int key))
            {
                return;
            }
            // 打掉一半（不全歼）：打掉核心的这一波有击毁，总结页“累计”必须含它（审查 P1）
            List<int> units = GroupUnits(key);
            for (int i = 0; i < units.Count / 2; i++)
            {
                Site.Kernel.Kill(units[i], 0);
            }
            F.StepUntil(() => RaidResultService.All(s).Any(r => r != null && r.PlanId == p.PlanId && r.Killed > 0), 30);
            BuildingRecord core = s.BuildingRecords.FirstOrDefault(b => b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            bool lost = RaidResultService.OnCoreDestroyed(s, core); // 正式入口（攻城对账 CheckCore 判定核心到下限后调用的同一个边沿）
            long destroyedAt = GameClock.Ticks;
            int killedLast = RaidResultService.All(s).Where(r => r != null && r.PlanId == p.PlanId).Sum(r => r.Killed);
            int days = PostgameService.HoldDays(s, GameClock.Ticks);
            PostgamePurgeRecord rec = st.History.LastOrDefault();
            string title = string.Empty, sub = string.Empty, stats = string.Empty, totals = PostgameService.TotalsLine(s);
            bool pageOk = false;
            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(FailureUxml).CloneTree();
            var go = new GameObject("__FgPostgameFailure") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // 真实顺序：核心被毁后不再走任何一步，失败页就弹出（Update 每帧轮询 IsCoreDestroyed，只在第一次显示时写统计段）
                HomeValleyFailureUIToolkit page = go.AddComponent<HomeValleyFailureUIToolkit>();
                page.BindView(tree);
                page.Apply(true);
                title = page.TitleText;
                sub = page.SubtitleText;
                stats = page.PostgameStatsText;
                pageOk = page.Shown && title == GameText.Get("postgame.end.title") && sub.Contains($"{days} 天") && stats.Contains($"坚守 {days} 天") && stats.Contains("最高清洗等级 1")
                         && stats.Contains("最佳单次防守") && stats.Contains(totals) && page.LoadButton != null && page.LoadButton.enabledSelf && !GameText.ContainsMarker(title + sub + stats);
                page.Apply(false);
            }
            finally
            {
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
            bool ended = lost && st.EndTick == destroyedAt && PostgameService.Ended(s) && !PostgameService.Active(s) && rec != null && rec.Outcome == PostgameService.OutcomeCoreLost && days >= 7
                         && killedLast > 0 && rec.Killed == killedLast && st.TotalKilled == killedLast;
            GameClock.SkipForTests(s, RaidDirectorService.DayTicks(8));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool frozen = st.Launched == 1 && PostgameService.HoldDays(s, GameClock.Ticks) == days && st.TotalKilled == killedLast;
            Expect(saved.Success && ended && frozen && pageOk,
                $"H1 后日谈中核心被突袭摧毁（FGR-SBX-005）：同一步坚守结束（第 {st.EndTick} 步 = 被毁第 {destroyedAt} 步，进行中那一次记“{rec?.Outcome}”、击毁 {rec?.Killed} = 结算 {killedLast}、累计 {st.TotalKilled}）（{ended}），"
                + $"之后不再来清洗、天数与统计不再变（{frozen}）；失败页在被毁后第一帧弹出就含最后一波：“{title}｜{sub}｜{stats.Replace('\n', ' ')}”、可以读取自动存档（{pageOk}）");
            CampaignState l = RestoreSlot(out string fail);
            PostgameState lt = PostgameService.StateOf(l);
            Expect(l != null && !HomeValleySoftlockGuard.IsCoreDestroyed(l) && lt.EndTick < 0 && PostgameService.Active(l) && lt.StartTick == start && lt.ActiveIndex == 0,
                $"H2 读取自动存档继续：核心完好、坚守没有结束、起算点不变（{lt?.StartTick} = {start}）、进行中的第 1 级清洗还在（{fail}）");
        }

        // ─────────────────────────────── I 轨道坠落与遗产 ───────────────────────────────

        private static void CheckOrbitalAndLegacy()
        {
            CampaignState s = NewPostgame(14018, out _);
            EventDirectorService.Install();
            EventDirectorService.Register(new OrbitalDropService.Handler()); // 正式流程由 GameRoot 装好全部事件处理器；自检里只要轨道坠落
            s.Progress.Act = 3;
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            s.Progress.IsPostgame = false;
            long dueOff = EventDirectorService.DueTick(s, OrbitalDropService.EventId);
            double mulOff = PostgameService.EventRateMul(s, OrbitalDropService.EventId);
            s.Progress.IsPostgame = true;
            long dueOn = EventDirectorService.DueTick(s, OrbitalDropService.EventId);
            EventDirectorScheduleRecord rec = EventDirectorService.Schedule(s, OrbitalDropService.EventId);
            bool rate = mulOff == 1.0 && PostgameService.EventRateMul(s, OrbitalDropService.EventId) == PostgameService.OrbitalDropRate
                        && PostgameService.EventRateMul(s, "worm") == 1.0 && rec != null && dueOn < dueOff && dueOn != long.MaxValue
                        && Math.Abs((dueOn - rec.AnchorTick) * (double)PostgameService.OrbitalDropRate - (dueOff - rec.AnchorTick)) <= PostgameService.OrbitalDropRate + 1;
            Expect(rate, $"I1 后日谈里轨道坠落更频繁（FGR-SBX-004）：随机间隔 ÷ {PostgameService.OrbitalDropRate}（到期步 {dueOff} → {dueOn}，锚点 {rec?.AnchorTick}）；其它事件不变");

            HeritageService.EnsurePlaced(s);
            HomeExplorationState hx = HomeExplorationService.StateOf(s);
            var pg = HeritageService.All.Where(HeritageService.PostgameOnly).ToList();
            bool unplaced = pg.All(h => Array.IndexOf(hx.HeritageIds, h.Id) < 0 && !HeritageService.CanTrade(s, h.Id) && HeritageService.RowText(s, h.Id).Contains("后日谈"));
            int yes = Enumerable.Range(1, 400).First(i => PostgameService.RollsLegacy(s, i));
            int no = Enumerable.Range(1, 400).First(i => !PostgameService.RollsLegacy(s, i));
            var at = new Vector2(HomeValleyLayout.Core.Position.x + 20f, HomeValleyLayout.Core.Position.y + 5f);
            bool none = PostgameService.OnOrbitalDrop(s, at, no) == null;
            var got = new List<string>();
            for (int i = 0; i < pg.Count + 1; i++)
            {
                got.Add(PostgameService.OnOrbitalDrop(s, at, yes)?.Id ?? "（无）");
            }
            bool order = got.Take(pg.Count).SequenceEqual(pg.Select(h => h.Id)) && got[pg.Count] != "（无）" && !HeritageService.PostgameOnly(HeritageService.Find(got[pg.Count]))
                         && pg.All(h => HeritageService.StatusOf(s, h.Id) == HeritageStatus.OnGround && HeritageService.GroundAt(s, at, 2f) != null);
            RegionQuestItemRecord q = HeritageService.FindItem(s, pg[0].Id);
            HeritageService.OnDelivered(s, q);
            bool delivered = HeritageService.IsObtained(s, pg[0].Id);
            s.Progress.IsPostgame = false;
            bool notPostgame = PostgameService.OnOrbitalDrop(s, at, yes) == null;
            s.Progress.IsPostgame = true;
            Expect(unplaced && none && order && delivered && notPostgame,
                $"I2 最后一组人类遗产只在后日谈获得：不放进遗迹、商队不卖（{unplaced}）；坠落残骸按种子带来遗产（序号 {no} 不带：{none}），先是专属的 {pg.Count} 件、之后是还没找到的普通遗产：{string.Join(" → ", got)}；" +
                $"落在地上可派机器取、带回入库（{delivered}）；不在后日谈不带（{notPostgame}）");
        }

        // ─────────────────────────────── 辅助 ───────────────────────────────

        /// <summary>新世界 → 写结局账本 → 打开结算 → 点“继续”（正式入口 EndingService.ContinuePostgame）→ 后日谈开始。</summary>
        private static CampaignState NewPostgame(int seed, out long start, bool observe = true)
        {
            CampaignState s = NewWorld(seed, observe);
            s.Progress.Act = 3;
            _introEvents = 0;
            CampaignEventLedger.TryGrant(s, EndingService.EndingEventId, "Endgame", s.PlaySeconds, "test");
            bool opened = EndingService.OpenSettlement(s);
            start = GameClock.Ticks;
            bool cont = EndingService.ContinuePostgame();
            if (!opened || !cont)
            {
                Fail($"前置：结算打开 {opened}、继续 {cont}（种子 {seed}）");
            }
            return s;
        }

        /// <summary>等到第 <paramref name="index"/> 次清洗交给导演并建好计划。</summary>
        private static RaidPlanRecord LaunchAndPlan(CampaignState s, int index)
        {
            PostgameState st = PostgameService.StateOf(s);
            if (st.NextPurgeTick - 1 > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, st.NextPurgeTick - 1 - GameClock.Ticks);
            }
            bool ok = F.StepUntil(() => st.ActiveIndex == index && st.ActivePlanId.Length > 0, 30);
            RaidPlanRecord p = ok ? RaidDirectorService.FindPlan(s, st.ActivePlanId) : null;
            if (p == null)
            {
                Fail($"第 {index + 1} 级清洗没有建出计划（已来 {st.Launched}、进行中 {st.ActiveIndex}、计划 {st.ActivePlanId}）");
            }
            return p;
        }

        private static bool Arrive(CampaignState s, RaidPlanRecord p, out int key)
        {
            key = 0;
            F.StepUntil(() => p.DepartTick >= 0, 120);
            if (p.DepartTick - 1 > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, p.DepartTick - 1 - GameClock.Ticks);
            }
            int k = 0;
            bool arrived = F.StepUntil(() =>
            {
                TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
                k = g != null ? SiegeService.KeyOf(g) : 0;
                return k != 0 && GroupUnits(k).Count > 0;
            }, 300);
            key = k;
            if (!arrived)
            {
                Fail($"清洗 {p.PlanId} 没有到达展开（计划状态 {p.State}，队伍 {p.GroupId}）");
            }
            return arrived;
        }

        /// <summary>到达 → 全歼（含登陆舱投放的）→ 后日谈记撑过。</summary>
        private static bool RunToCleared(CampaignState s, RaidPlanRecord p, int cleared, out int killed)
        {
            killed = 0;
            PostgameState st = PostgameService.StateOf(s);
            if (!Arrive(s, p, out int key))
            {
                return false;
            }
            CombatSite site = Site;
            bool done = F.StepUntil(() =>
            {
                foreach (int x in GroupUnits(key))
                {
                    site.Kernel.Kill(x, 0);
                }
                return st.Cleared == cleared;
            }, 240);
            if (!done)
            {
                Fail($"第 {cleared} 级清洗全歼后没有记撑过（计划 {RaidDirectorService.FindPlan(s, p.PlanId)?.State}、进行中 {st.ActiveIndex}、核心被毁 {HomeValleySoftlockGuard.IsCoreDestroyed(s)}）");
            }
            killed = st.History.LastOrDefault()?.Killed ?? 0;
            return done;
        }

        private static string Sig(CampaignState s)
        {
            PostgameState st = PostgameService.StateOf(s);
            var sb = new StringBuilder();
            sb.Append(s.Progress.IsPostgame).Append('|').Append(st.StartTick).Append('|').Append(st.NextPurgeTick).Append('|').Append(st.RetryTick).Append('|').Append(st.Launched).Append('|').Append(st.Cleared)
              .Append('|').Append(st.ActiveIndex).Append('|').Append(st.ActivePlanId).Append('|').Append(st.HighestLevel).Append('|').Append(st.BestKilled).Append('|')
              .Append(st.TotalKilled).Append('|').Append(st.EndTick);
            foreach (PostgamePurgeRecord r in st.History)
            {
                sb.Append(";h").Append(r.Index).Append(',').Append(r.Level).Append(',').Append(r.LaunchTick).Append(',').Append(r.Outcome).Append(',').Append(r.Budget)
                  .Append(',').Append(r.Units).Append(',').Append(r.Strength.ToString("R")).Append(',').Append(r.Killed);
            }
            foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
            {
                if (p != null && p.PostgameIndex >= 0)
                {
                    sb.Append(";p").Append(p.PlanId).Append(',').Append(p.PostgameIndex).Append(',').Append(p.Budget).Append(',').Append(p.State).Append(',').Append(p.Occupancy)
                      .Append(',').Append(p.Strength.ToString("R")).Append(',').Append(string.Join("/", p.UnitCounts));
                }
            }
            return sb.ToString();
        }

        private static bool Notified(string key, out string text)
        {
            string prefix = GameText.Get(key);
            int cut = prefix.IndexOf('{');
            prefix = cut > 0 ? prefix.Substring(0, cut) : prefix;
            NotificationMember hit = null;
            foreach (NotificationEntry n in NotificationCenter.History)
            {
                if (n?.Type == null || n.Type.Id != PostgameService.NotifyType)
                {
                    continue;
                }
                foreach (NotificationMember m in n.Members)
                {
                    if (m?.DetailText != null && m.DetailText.Contains(prefix))
                    {
                        hit = m;
                    }
                }
            }
            text = hit?.DetailText ?? string.Empty;
            return hit != null;
        }

        private static List<int> GroupUnits(int key)
        {
            var list = new List<int>();
            CombatKernel k = Site?.Kernel;
            for (int i = 0; k != null && i < k.SlotCount; i++)
            {
                CombatUnitView v = k.ViewAt(i);
                if (v.Alive && v.Siege.Role != 0 && v.Siege.Group == key)
                {
                    list.Add(v.Id);
                }
            }
            return list;
        }

        /// <summary>D2 参照：同一 def / 精英标记、只乘当前难度倍率生成一台（读完即删），返回它的最大耐久与武器伤害。</summary>
        private static (float Hp, float Dmg) Ref(CombatSite site, int key, Vector2 at, RaidUnitDef def, bool elite, CampaignState s)
        {
            int id = SiegeService.SpawnOne(site, key, at, HomeValleyLayout.Core.Position, def, elite, DifficultyService.EnemyHealthMul(s), DifficultyService.EnemyDamageMul(s));
            (float, float) r = site.Kernel.TryGetUnit(id, out CombatUnitView v) && site.Kernel.TryGetWeapon(v.Weapon, out CombatWeapon w) ? (v.MaxHealth, w.Damage) : (0f, 0f);
            site.Kernel.Kill(id, 0);
            return r;
        }

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

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

        private static CampaignState NewWorld(int seed, bool observe = true)
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
            CampaignState s = CampaignState.CreateNew("fgpostgame-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgPostgameSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
                EndingService.ResetForTests();
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
