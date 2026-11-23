using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Platform;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Signal;
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
using TEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-SYS-04 Steam 集成与成就（FG15 FGR-SYS-080 / 081、FG19 FGR-EMG-183；验收 FGT-SYS-007 / FGT-SBX-005）自检：
    /// A 数据（清单、规则、阈值、组合图鉴 N ≤ 具名交互条目数、核心固件名单 = 6、首领、剧情类标记）；B 成就触发条件逐条（按存档里的真实判据，满足前不解锁、满足后解锁）；
    /// C 真实世界（第一步补判、第一幕阵亡走 MachineRegistry、产线无人介入按游戏分钟、规划操作清零、暂停不走、0.5x～3x 按游戏时间）；
    /// D 沙盒不计入剧情类（含后日谈 / 严酷 / 第一幕无损），系统类照常；E 严酷按难度修改记录；F 旧档补发（结局账本、第一幕阵亡时刻）；
    /// G 离线解锁 → 联网同步、平台已有的并回账本、账本只增不减并落盘；H 云存档冲突选哪边都不丢成就、Steam 提供者未接入 = 离线；
    /// I 富状态（幕 / 区域 / 沙盒 / 主菜单，只在变化时推送）与覆盖层按失焦暂停；J 存读档；K 成就面板（分组、图标、进度、沙盒说明、离线说明）与布局探针。
    /// </summary>
    public static class FgAchievementSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string PanelUxml = "Assets/GameRes/Raw/UI/UiKit/AchievementPanel.uxml";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("QA/自检/FG 成就与 Steam 集成")]
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
            Line("\n[成就与 Steam] 成就清单与判定、沙盒不计入剧情类、严酷按修改记录、离线解锁联网同步、云存档冲突不丢成就、富状态、覆盖层（FG15-SYS-04）");
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
            IPlatformProvider originalProvider = AchievementService.Provider;
            ICloudSaveProvider originalCloud = CloudSaveService.Provider;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgachievement-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                AchievementService.Install();

                Step(CheckData);
                Step(CheckRules);
                Step(CheckRulesMore);
                Step(CheckRealWorld);
                Step(CheckSandbox);
                Step(CheckHarsh);
                Step(CheckOldSave);
                Step(CheckOfflineSync);
                Step(CheckCloudAndSteam);
                Step(CheckPresenceAndOverlay);
                Step(CheckSaveLoad);
                Step(CheckPanel);
            }
            finally
            {
                PlatformServices.Bind(originalProvider);
                CloudSaveService.Provider = originalCloud;
                AchievementService.FilePathOverrideForTests = null;
                AchievementService.ResetForTests();
                FocusPolicy.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
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
            Line(_fail == 0 ? $"  成就与 Steam：全部 {_pass} 项通过" : $"  成就与 Steam：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── 每一节用一份新的临时账本（离线） ──
        private static void FreshLedger(string name, IPlatformProvider provider = null)
        {
            AchievementService.FilePathOverrideForTests = Path.Combine(_dir, name + ".json");
            AchievementService.ResetForTests();
            PlatformServices.Bind(provider);
        }

        private static CampaignState Small(string id, string difficulty = "Standard")
        {
            CampaignState s = CampaignState.CreateNew(id, difficulty, 7);
            CampaignFgStateDomains.EnsureAll(s);
            return s;
        }

        private static bool U(string id) => AchievementService.IsUnlocked(id);

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            IReadOnlyList<Achievement> rows = AchievementService.Rows;
            int reactions = MechanicCodex.Entries.Count(e => e.Kind == MechanicCodexKind.Reaction);
            Achievement codex = rows.FirstOrDefault(a => a.Rule == "codex_interactions");
            Achievement core = rows.FirstOrDefault(a => a.Rule == "core_firmware_all");
            var cats = rows.Select(a => a.Category).Distinct().ToList();
            bool texts = rows.All(a => !GameText.ContainsMarker(GameText.Get(a.NameKey)) && !GameText.ContainsMarker(GameText.Get(a.DescKey)));
            bool bosses = rows.Where(a => a.Rule == "boss_defeated").All(a => IntelCatalog.TryGetBoss(a.Arg, out _)) && rows.Count(a => a.Rule == "boss_defeated") == 4;
            bool story = rows.Where(a => a.Category == "story" || a.Category == "postgame").All(a => a.Story) && rows.Where(a => a.Category == "system" || a.Category == "economy").All(a => !a.Story);
            bool api = rows.Select(a => a.ApiName).Distinct().Count() == rows.Count && rows.All(a => !string.IsNullOrEmpty(a.ApiName));
            Expect(rows.Count >= 25 && rows.Count <= 40 && cats.Count == 5 && texts && bosses && story && api,
                $"A1 成就清单 {rows.Count} 条（FGR-SYS-081 表全部 30 条，“约 36”为初值）、5 类、文本键全部有译文、4 个阵营首领可解析、剧情 / 后日谈全部标“剧情类”、Steam API 名唯一");
            Expect(codex != null && codex.Threshold >= 1 && codex.Threshold <= reactions && reactions >= 20,
                $"A2 组合图鉴阈值 N = {codex?.Threshold}（FGR-EMG-183 由本卡定）≤ 图鉴里具名交互条目 {reactions} 条（按规则表规模取约 70%）");
            Expect(core != null && core.Threshold == FirmwareKinds.CoreRosterIds.Count && core.Threshold == 6,
                $"A3 “集齐 6 条核心固件”阈值 {core?.Threshold} = 核心固件名单 {FirmwareKinds.CoreRosterIds.Count} 条");
        }

        // ─────────────────────────────── B 逐条触发条件 ───────────────────────────────

        private static void CheckRules()
        {
            FreshLedger("rules");
            CampaignState s = Small("fgach-rules");
            int first = AchievementService.Evaluate(s);
            Expect(first == 0 && AchievementService.UnlockedTotal == 0, $"B0 新战役什么都没做：一个成就也不解锁（{first}）");

            var results = new List<string>();
            bool all = true;
            void Case(string id, Action make, Action half = null)
            {
                bool before = false;
                if (half != null)
                {
                    half();
                    AchievementService.Evaluate(s);
                    before = U(id);
                }
                make();
                AchievementService.Evaluate(s);
                bool ok = !before && U(id);
                all &= ok;
                results.Add($"{id}{(ok ? "✓" : "✗")}");
            }

            Case("act1_complete", () => s.Progress.Act = 2);
            Case("act2_complete", () => s.Progress.Act = 3);
            Case("revealed", () => CampaignEventLedger.TryGrant(s, RevealService.RevealedEventId, "Endgame", 0f));
            Case("vanguard_repelled", () => CampaignEventLedger.TryGrant(s, EndingService.EndingEventId, "Endgame", 0f));
            Case("first_uplink", () => s.MachineRecords = new[] { new MachineRecord { LogicId = 9001, IsAlive = true, SignalUplinkCount = 1 } });
            Case("core_firmware_all", () => s.PrimitiveChips = FirmwareKinds.CoreRosterIds.Select(id => new PrimitiveChipRecord { CardDefId = id }).ToArray(),
                () => s.PrimitiveChips = FirmwareKinds.CoreRosterIds.Take(5).Select(id => new PrimitiveChipRecord { CardDefId = id }).ToArray());
            Case("fusion_first", () => s.Research.Fusion.Discovered = new[] { "r0" });
            Case("fusion_ten", () => s.Research.Fusion.Discovered = Enumerable.Range(0, 10).Select(i => "r" + i).ToArray(),
                () => s.Research.Fusion.Discovered = Enumerable.Range(0, 9).Select(i => "r" + i).ToArray());
            Case("crack_twenty", () => s.Research.Analysis.Completed = 20, () => (s.Research.Analysis ??= new AnalysisBenchState()).Completed = 19);
            Case("raid_repelled_away", () =>
            {
                s.Stats.AwayReports.HasOpen = true;
                AchievementService.OnRaidClosed(s, RaidResultService.OutcomeWithdrawn);
            }, () =>
            {
                AchievementService.OnRaidClosed(s, RaidResultService.OutcomeDestroyed); // 负向：人在家（没有进行中的离家报告）
                s.Stats.AwayReports.HasOpen = true;
                AchievementService.OnRaidClosed(s, RaidResultService.OutcomeCoreLost); // 负向：核心被毁不算击退
                s.Stats.AwayReports.HasOpen = false;
            });
            Case("worm_kill", () => VibrationService.StateOf(s).Kills = 1);
            Case("reverse_hijack_ten", () => AchievementService.OnReverseHijack(s), () =>
            {
                for (int i = 0; i < 9; i++)
                {
                    AchievementService.OnReverseHijack(s);
                }
            });
            Case("silent_nights_ten", () => s.Weather.SilentNight.Active = false, () =>
            {
                s.Weather.SilentNight.Count = 10;
                s.Weather.SilentNight.Active = true; // 第 10 次正在进行：还没撑过
            });
            Case("return_100000", () => SetReached(s, "m100000"), () => SetReached(s, "m50000"));
            Case("return_5000", () => SetReached(s, "m5000"));
            Case("return_20000", () => SetReached(s, "m20000"));
            Expect(all, "B1 触发条件逐条（FGT-SYS-007，满足前不解锁、满足那一刻解锁）：" + string.Join(" ", results));

            // 首领：读 IntelService 的击败判据（洗礼塔 / 余烬主机 / 寂听主脑的状态在各自首领域里）；这里核对“没击败 = 不解锁”，逐个击败的正向在 B5。
            bool bossesLocked = AchievementService.Rows.Where(a => a.Rule == "boss_defeated").All(a => !U(a.Id) && AchievementService.Progress(a, s, out _) == 0);
            Achievement codexRow = AchievementService.Find("codex_interactions");
            int cur = AchievementService.Progress(codexRow, s, out int need);
            Expect(bossesLocked && cur == MechanicCodex.UnlockedCountOf(MechanicCodexKind.Reaction) && (cur >= need) == U("codex_interactions"),
                $"B2 首领未击败不解锁；组合图鉴进度读跨存档图鉴的具名交互发现数 {cur} / {need}（沙盒同样计入，系统类）");

            // 后日谈：坚守天数与撑过的最高清洗等级（只读，不另算）
            PostgameState pg = PostgameService.StateOf(s) ?? (s.Progress.Postgame = new PostgameState());
            s.Progress.IsPostgame = true;
            pg.StartTick = GameClock.Ticks;
            pg.HighestCleared = 9;
            GameClock.SkipForTests(s, RaidDirectorService.DayTicks(29));
            AchievementService.Evaluate(s);
            bool notYet = !U("hold_30") && !U("purge_10");
            pg.HighestCleared = 10;
            GameClock.SkipForTests(s, RaidDirectorService.DayTicks(1));
            AchievementService.Evaluate(s);
            Expect(notYet && U("hold_30") && U("purge_10") && !U("hold_100") && !U("purge_20"),
                $"B3 后日谈：坚守 29 天 / 撑过 9 级不解锁 → 30 天 / 10 级解锁（读 PostgameService.HoldDays = {PostgameService.HoldDays(s, GameClock.Ticks)}、HighestCleared）；100 天 / 20 级未到");
            GameClock.ResetSession();
            Expect(AchievementService.UnlockCount == AchievementService.UnlockedTotal && AchievementService.LastUnlocked != null,
                $"B4 每条只解锁一次（{AchievementService.UnlockCount} 次 = 账本 {AchievementService.UnlockedTotal} 条）");
        }

        private static void SetReached(CampaignState s, string milestone)
        {
            PurificationState p = s.Weather.Purification ??= new PurificationState();
            p.Reached = (p.Reached ?? Array.Empty<string>()).Append(milestone).ToArray();
        }

        /// <summary>B5～B9（修复第 1 轮）：B1 没覆盖到的 4 个首领、组合图鉴 N、单品千件、“一台不少”实时路径、副队离家时击退突袭——逐条正向 + 差一不触发。</summary>
        private static void CheckRulesMore()
        {
            // B5 首领：按各首领域里的正式击败状态（铸造前哨核心 = 区域记录 CoreState 已摧毁；寂听主脑 / 洗礼塔 / 余烬主机 = 首领域 Phase = Defeated），一个一个击败。
            FreshLedger("bosses");
            CampaignState s = Small("fgach-bosses");
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            List<Achievement> bossRows = AchievementService.Rows.Where(a => a.Rule == "boss_defeated").ToList();
            var bossResults = new List<string>();
            bool bossesOk = bossRows.Count == 4;
            var done = new HashSet<string>();
            foreach (Achievement a in bossRows)
            {
                AchievementService.Evaluate(s);
                bool lockedBefore = !U(a.Id);
                if (!IntelCatalog.TryGetBoss(a.Arg, out IntelBossDef b))
                {
                    bossesOk = false;
                    bossResults.Add(a.Id + "✗(首领表里没有 " + a.Arg + ")");
                    continue;
                }
                switch (b.Rule)
                {
                    case IntelCatalog.BossRuleFoundryCore: FoundryOutpostRegion.Find(s).CoreState = CoreBossState.Destroyed.ToString(); break;
                    case IntelCatalog.BossRuleSilentMind: SilentMindBoss.Record(s).Phase = (int)SilentMindPhase.Defeated; break;
                    case IntelCatalog.BossRuleBaptismTower: BaptismTowerBoss.Record(s).Phase = (int)BaptismPhase.Defeated; break;
                    case IntelCatalog.BossRuleEmberHost: EmberHostBoss.Record(s).Phase = (int)EmberPhase.Defeated; break;
                }
                AchievementService.Evaluate(s);
                done.Add(a.Id);
                bool othersLocked = bossRows.Where(o => !done.Contains(o.Id)).All(o => !U(o.Id));
                bool ok = lockedBefore && U(a.Id) && IntelService.BossDefeated(s, b) && othersLocked;
                bossesOk &= ok;
                bossResults.Add($"{a.Id}({b.Rule}){(ok ? "✓" : "✗")}");
            }
            Expect(bossesOk, "B5 4 个阵营首领逐个击败（IntelService.BossDefeated 读各首领域的正式状态）：击败前不解锁、击败那一刻只解锁这一个：" + string.Join(" ", bossResults));

            // B6 组合图鉴 N：临时图鉴文件里经 MechanicCodex.Unlock（正式解锁入口）解锁具名交互 N−1 条 → 不解锁；第 N 条 → 解锁。
            string codexPath = MechanicCodex.FilePathOverrideForTests;
            MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex-b6.json");
            MechanicCodex.Reload();
            try
            {
                FreshLedger("codex");
                CampaignState c = Small("fgach-codex");
                Achievement row = AchievementService.Find("codex_interactions");
                int need = row?.Threshold ?? int.MaxValue;
                List<string> reactions = MechanicCodex.Entries.Where(e => e.Kind == MechanicCodexKind.Reaction).Select(e => e.Id).ToList();
                bool enough = reactions.Count >= need;
                for (int i = 0; enough && i < need - 1; i++)
                {
                    MechanicCodex.Unlock(reactions[i]);
                }
                AchievementService.Evaluate(c);
                int curBefore = AchievementService.Progress(row, c, out _);
                bool lockedAtNm1 = !U("codex_interactions");
                if (enough)
                {
                    MechanicCodex.Unlock(reactions[need - 1]);
                }
                AchievementService.Evaluate(c);
                int curAfter = AchievementService.Progress(row, c, out _);
                Expect(enough && curBefore == need - 1 && lockedAtNm1 && curAfter == need && U("codex_interactions"),
                    $"B6 组合图鉴（FGR-EMG-183）：具名交互发现 {curBefore} / {need} 不解锁（差一），{curAfter} / {need} 解锁；图鉴里共 {reactions.Count} 条");
            }
            finally
            {
                MechanicCodex.FilePathOverrideForTests = codexPath;
                MechanicCodex.Reload();
            }

            // B7 单品千件：统计面板 1 分钟窗口（ProductionStats，正式记账入口 RecordUnits / RecordFluidMl）的产出速率。统计开始 30 游戏秒（分母 = 0.5 分钟，窗口覆盖 58 秒以上）：
            // 固体 499 件 = 每分钟 998（差一件）不解锁、500 件 = 1000 解锁；流体 499.5 升 = 每分钟 999 升不解锁、500 升 = 1000 升。
            ItemDef solid = ItemCatalog.Items.FirstOrDefault(i => i.Form != ItemForm.Fluid);
            ItemDef fluid = ItemCatalog.Items.FirstOrDefault(i => i.Form == ItemForm.Fluid);
            Achievement rate = AchievementService.Find("item_rate_1000");
            FreshLedger("rate");
            GameClock.ResetSession();
            GameClock.SkipForTests(null, GameClock.TicksFor(120));
            float minutes = -1f;
            int ProgressWith(ItemDef item, long amount, bool ml, bool evaluate)
            {
                CampaignState r = Small("fgach-rate-" + item?.Id + "-" + amount);
                if (ml)
                {
                    ProductionStats.RecordFluidMl(r, item, amount, true);
                }
                else
                {
                    ProductionStats.RecordUnits(r, item, amount, true);
                }
                r.Stats.Production.StartTick = GameClock.Ticks - GameClock.TicksFor(30);
                minutes = ProductionStats.EffectiveSeconds(r, 0) / 60f;
                if (evaluate)
                {
                    AchievementService.Evaluate(r);
                }
                return AchievementService.Progress(rate, r, out _);
            }
            int s998 = ProgressWith(solid, 499, false, true);
            bool locked998 = !U("item_rate_1000");
            int s1000 = ProgressWith(solid, 500, false, true);
            bool unlocked1000 = U("item_rate_1000");
            int f999 = fluid != null ? ProgressWith(fluid, 499500, true, false) : -1;
            int f1000 = fluid != null ? ProgressWith(fluid, 500, false, false) : -1; // RecordUnits 对流体按升
            GameClock.ResetSession();
            Expect(rate != null && rate.Threshold == 1000 && Mathf.Approximately(minutes, 0.5f) && s998 == 998 && locked998 && s1000 == 1000 && unlocked1000 && f999 == 999 && f1000 == 1000,
                $"B7 单品千件（读统计面板 1 分钟窗口，分母 {minutes:0.###} 分钟）：固体 {solid?.Id} 每分钟 {s998} 不解锁、{s1000} 解锁；流体 {fluid?.Id} 每分钟 {f999} / {f1000} 升（阈值 {rate?.Threshold}）");

            // B8 “一台不少”实时路径：新战役第一幕没有阵亡 → 进入第二幕（NarrativeService 推进幕调 OnActChanged）当场解锁。
            FreshLedger("flawless");
            CampaignState f = Small("fgach-flawless");
            AchievementService.Evaluate(f);
            bool tracked = f.Achievements.Act1Tracked && f.Achievements.Act1Losses == 0;
            bool lockedInAct1 = !U("act1_flawless");
            f.Progress.Act = 2;
            AchievementService.OnActChanged(f, 2);
            Expect(tracked && lockedInAct1 && f.Achievements.Act1Flawless && U("act1_flawless") && U("act1_complete"),
                "B8 新战役第一幕一台没丢：第一幕里不解锁，进入第二幕当场解锁“一台不少”（与 C2 第一幕阵亡一台不解锁成对）");

            // B9 多支远征队：主队在家、副队离家时家园击退突袭同样计数；都在家不计。
            FreshLedger("raid-extra");
            CampaignState x = Small("fgach-raid-extra");
            x.Grid.ExpeditionTeam.Extras = new[] { new ExpeditionExtraTeam { AwayHasOpen = false } };
            AchievementService.OnRaidClosed(x, RaidResultService.OutcomeDestroyed);
            int home = x.Achievements?.RaidsRepelledAway ?? 0;
            x.Grid.ExpeditionTeam.Extras[0].AwayHasOpen = true;
            AchievementService.OnRaidClosed(x, RaidResultService.OutcomeDestroyed);
            AchievementService.Evaluate(x);
            Expect(home == 0 && !x.Stats.AwayReports.HasOpen && x.Achievements.RaidsRepelledAway == 1 && U("raid_repelled_away"),
                $"B9 主队在家、副队离家报告开着时击退突袭 → 计数 {x.Achievements.RaidsRepelledAway} 并解锁；全部在家时不计（{home}）");
        }

        // ─────────────────────────────── C 真实世界 ───────────────────────────────

        private static void CheckRealWorld()
        {
            FreshLedger("world");
            CampaignState s = NewWorld(15041);
            AchievementCampaignState st = s.Achievements;
            Expect(st.Initialized && st.Act1Tracked && st.Act1Losses == 0 && AchievementService.EvaluateCount > 0 && st.NextEvalTick > GameClock.Ticks,
                $"C1 新战役第一步就判一次（判了 {AchievementService.EvaluateCount} 次），从第一幕开始记阵亡（{st.Act1Tracked}），下一次按游戏分钟（第 {st.NextEvalTick} 步）");

            MachineRecord m = s.MachineRecords.FirstOrDefault(r => r != null && r.IsAlive);
            if (m == null)
            {
                Fail("前置：新战役里没有存活的机器");
                return;
            }
            MachineRegistry.MarkDeadByLogicId(m.LogicId);
            int losses = st.Act1Losses;
            s.Progress.Act = 2;
            AchievementService.OnActChanged(s, 2);
            Expect(losses == 1 && st.Act1Decided && !st.Act1Flawless && U("act1_complete") && !U("act1_flawless"),
                $"C2 第一幕里阵亡一台（经 MachineRegistry.MarkDeadByLogicId，记 {losses}）→ 进入第二幕当场判：解锁“第一幕落幕”，不解锁“一台不少”");

            // 引导钩子（正式路径 GuidanceHooks.Raise，每次都回调 Touched）→ 记进本战役 → 下一步判。
            bool beltBefore = U("first_belt");
            GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstBelt);
            WorldSimulation.StepMany(1);
            Expect(!beltBefore && U("first_belt") && st.Hooks.Contains(GuidanceHooks.LogisticsFirstBelt),
                "C2b 放下传送带（钩子 logistics.belt.first_placed 经 GuidanceHooks.Raise）→ 记进本战役、下一步解锁“第一条传送带”");

            // 产线无人介入：每游戏分钟有产出、规划步号不变 → +1；规划操作清零；30 分钟解锁。
            ItemDef item = ItemCatalog.Items.First();
            int Minute(bool produce)
            {
                long due = st.NextEvalTick;
                GameClock.SkipForTests(s, Math.Max(0, due - GameClock.Ticks - 3));
                if (produce)
                {
                    ProductionStats.RecordUnits(s, item, 5, true); // 这一分钟的产出（1 分钟统计窗口内）
                }
                for (int k = 0; k < 8 && st.NextEvalTick == due; k++)
                {
                    WorldSimulation.StepMany(1); // 走到分钟边界那一步
                }
                return st.LineMinutes;
            }
            Minute(true);
            int a1 = Minute(true);
            int a2 = Minute(true);
            PlanHistory.BeginStep(s, PlanStepKind.Place);
            PlanHistory.EndStep(s);
            int afterPlan = Minute(true);
            Minute(false);
            int afterIdle = Minute(false);
            Expect(a2 == a1 + 1 && afterPlan == 0 && afterIdle == 0,
                $"C3 产线无人介入按游戏分钟累加（{a1} → {a2}）；做一次施工规划操作清零（{afterPlan}）；这一分钟没有产出也清零（{afterIdle}）");
            for (int i = 0; i < 31 && !U("line_unattended"); i++)
            {
                Minute(true);
            }
            Expect(U("line_unattended") && st.LineMinutes >= 30, $"C4 连续 {st.LineMinutes} 个游戏分钟有产出且没有规划操作 → 解锁“自动化”");

            // 暂停与倍速：判定只跟游戏时间走。
            int lm = st.LineMinutes;
            GameClock.SkipForTests(s, Math.Max(0, st.NextEvalTick - GameClock.Ticks - 12));
            ProductionStats.RecordUnits(s, item, 5, true);
            GameClock.SetPaused(true);
            for (int i = 0; i < 20; i++)
            {
                WorldSimulation.Frame(1f);
            }
            bool paused = st.LineMinutes == lm;
            GameClock.SetPaused(false);
            var speeds = new List<string>();
            bool speedOk = paused;
            foreach (float sp in new[] { 0.5f, 1f, 2f, 3f })
            {
                GameClock.SetSpeed(sp);
                GameClock.SkipForTests(s, Math.Max(0, st.NextEvalTick - GameClock.Ticks - 12));
                ProductionStats.RecordUnits(s, item, 5, true);
                int before = st.LineMinutes;
                RunFrames(10, sp); // 真实时间推进约 10 个游戏步：还没到这一分钟的边界
                bool notYet = st.LineMinutes == before;
                RunFrames(4, sp);
                bool crossed = st.LineMinutes == before + 1;
                speedOk &= notYet && crossed;
                speeds.Add($"{sp}x:{(notYet && crossed ? "✓" : "✗")}");
            }
            GameClock.SetSpeed(1f);
            Expect(speedOk, $"C5 暂停 20 秒真实时间分钟计数不动（{paused}）；0.5x～3x 都在同一个游戏分钟边界加一（{string.Join(" ", speeds)}）");
        }

        /// <summary>按真实帧推进：每帧的真实时间 = 1 个游戏步 ÷ 倍速（单帧时长有上限，所以分成多帧）。</summary>
        private static void RunFrames(int steps, float speed)
        {
            for (int i = 0; i < steps; i++)
            {
                WorldSimulation.Frame(1.0001f / (GameClock.StepHz * speed));
            }
        }

        // ─────────────────────────────── D 沙盒 ───────────────────────────────

        private static void CheckSandbox()
        {
            FreshLedger("sandbox");
            CampaignState s = Small("fgach-sbx");
            s.Progress.IsSandbox = true;
            s.Progress.Act = 3;
            CampaignEventLedger.TryGrant(s, EndingService.EndingEventId, "Endgame", 0f);
            CampaignEventLedger.TryGrant(s, RevealService.RevealedEventId, "Endgame", 0f);
            s.Research.Fusion.Discovered = new[] { "r0" };
            VibrationService.StateOf(s).Kills = 1;
            s.Progress.IsPostgame = true;
            (s.Progress.Postgame ??= new PostgameState()).HighestCleared = 25;
            AchievementService.Evaluate(s);
            var storyRows = AchievementService.Rows.Where(a => a.Story).ToList();
            bool noStory = storyRows.All(a => !U(a.Id));
            bool system = U("fusion_first") && U("worm_kill");
            Expect(!SandboxService.StoryAchievementsAllowed(s) && noStory && system && storyRows.Count >= 13,
                $"D1 沙盒存档（FGT-SBX-005）：剧情类 {storyRows.Count} 条（剧情、严酷、第一幕无损、后日谈）判据全满足也不解锁；系统 / 挑战类照常（第一次熔合 {U("fusion_first")}、掘进蠕虫 {U("worm_kill")}）");
            s.Progress.IsSandbox = false;
            AchievementService.Evaluate(s);
            Expect(U("vanguard_repelled") && U("purge_20"), "D2 对照：同样的判据在剧情存档里解锁（击退先遣舰队、抵挡 20 级清洗）");
        }

        // ─────────────────────────────── E 严酷 ───────────────────────────────

        private static void CheckHarsh()
        {
            FreshLedger("harsh");
            CampaignState kept = Small("fgach-harsh", DifficultyService.Harsh);
            CampaignState changed = Small("fgach-harsh2", DifficultyService.Harsh);
            changed.Raids.Difficulty.EverLeftStart = true; // 中途改到别的难度又改回严酷（修改记录里“离开过开局难度”）
            CampaignState standard = Small("fgach-std");
            foreach (CampaignState s in new[] { changed, standard })
            {
                CampaignEventLedger.TryGrant(s, EndingService.EndingEventId, "Endgame", 0f);
                AchievementService.Evaluate(s);
            }
            bool neg = !U("harsh_clear");
            AchievementService.Evaluate(kept);
            bool notCleared = !U("harsh_clear");
            CampaignEventLedger.TryGrant(kept, EndingService.EndingEventId, "Endgame", 0f);
            CampaignSession.Set(Slot, kept);
            GameEvent.Send(EndingService.EndingReachedEvent); // 正式路径：结局落盘后发的事件，成就当场判
            Expect(neg && notCleared && U("harsh_clear"),
                $"E1 严酷通关按难度修改记录（DifficultyService.KeptThroughout）：中途离开过严酷 / 标准难度通关不解锁（{neg}）；全程严酷没通关不解锁（{notCleared}）；全程严酷通关 → 结局事件当场解锁");
        }

        // ─────────────────────────────── F 旧档补发 ───────────────────────────────

        private static void CheckOldSave()
        {
            FreshLedger("oldsave");
            CampaignState s = Small("fgach-old");
            s.Achievements = null; // 本卡之前的存档：没有成就域
            s.Progress.Act = 3;
            CampaignEventLedger.TryGrant(s, EndingService.EndingEventId, "Endgame", 0f);
            s.Progress.Narrative = new[] { new NarrativeEntryRecord { Id = "n1", Act = 1, Tick = 100 }, new NarrativeEntryRecord { Id = "n2", Act = 2, Tick = 5000 } };
            s.MachineRecords = new[] { new MachineRecord { LogicId = 1, IsAlive = false, DeathTick = 6000 } }; // 第二幕才阵亡
            AchievementService.Evaluate(s);
            bool backfill = U("vanguard_repelled") && U("act1_complete") && U("act2_complete") && U("act1_flawless") && s.Achievements != null && s.Achievements.Act2Tick == 5000;

            FreshLedger("oldsave2");
            CampaignState t = Small("fgach-old2");
            t.Achievements = null;
            t.Progress.Act = 2;
            t.Progress.Narrative = new[] { new NarrativeEntryRecord { Id = "n2", Act = 2, Tick = 5000 } };
            t.MachineRecords = new[] { new MachineRecord { LogicId = 1, IsAlive = false, DeathTick = 4000 } }; // 第一幕阵亡
            AchievementService.Evaluate(t);
            bool lost = U("act1_complete") && !U("act1_flawless") && t.Achievements.Act1Losses == 1;

            FreshLedger("oldsave3");
            CampaignState u = Small("fgach-old3");
            u.Achievements = null;
            u.Progress.Act = 2; // 查不出第二幕从哪一步开始：宁可不发
            AchievementService.Evaluate(u);
            Expect(backfill && lost && !U("act1_flawless"),
                $"F1 旧档读入补发（FG13-END-06 承接）：结局账本 endgame_ending → 击退先遣舰队；第一幕无损按资料库第二幕开始时刻（{s.Achievements?.Act2Tick}）与阵亡时刻核对（第二幕阵亡 → 发，第一幕阵亡 → 不发 {lost}）；查不清 → 不发");
        }

        // ─────────────────────────────── G 离线 → 联网 ───────────────────────────────

        private static void CheckOfflineSync()
        {
            var steam = new LocalPlatformProvider { Online = false };
            steam.Achievements.Add("WORM_KILL"); // 另一台机器上已经解锁过
            FreshLedger("sync", steam);
            CampaignState s = Small("fgach-sync");
            s.Research.Fusion.Discovered = new[] { "r0" };
            AchievementService.Evaluate(s);
            AchievementLedgerEntry e = AchievementService.Entry("fusion_first");
            bool offline = e != null && !e.Synced && !steam.Achievements.Contains("FUSION_FIRST") && AchievementService.PendingSyncCount == 1 && File.Exists(AchievementService.FilePath);
            PlatformServices.Tick(PlatformServices.SyncRetrySeconds + 1f, null, false); // 还离线：什么也不推
            bool stillPending = AchievementService.PendingSyncCount == 1;

            steam.Online = true; // 联网
            PlatformServices.Tick(PlatformServices.SyncRetrySeconds + 1f, null, false);
            bool synced = steam.Achievements.Contains("FUSION_FIRST") && AchievementService.Entry("fusion_first").Synced && AchievementService.PendingSyncCount == 0 && U("worm_kill");
            AchievementService.ResetForTests(); // 重启游戏：从账本文件读回
            bool persisted = U("fusion_first") && AchievementService.Entry("fusion_first").Synced && U("worm_kill");
            steam.Achievements.Clear(); // 平台那边被清空 / 换了账号：本机账本只增不减，并且把标过已同步的重新推上去
            int repushed = AchievementService.SyncPending();
            bool monotonic = U("fusion_first") && U("worm_kill") && repushed == 2 && steam.Achievements.Contains("FUSION_FIRST") && steam.Achievements.Contains("WORM_KILL");
            Expect(offline && stillPending && synced && persisted && monotonic,
                $"G1 负向：离线时解锁 → 记进本机账本（等待同步 {offline}），离线时重试不丢（{stillPending}）；联网后自动补推（{synced}），并把平台上别处解锁的并回账本；重启读回（{persisted}）；平台列表变少（换账号 / 被重置）不删本机成就，已同步的重新推回平台 {repushed} 条（{monotonic}）");

            // 解锁时在线：当场推送；通知中心有“成就解锁”。
            VibrationService.StateOf(s).Kills = 1;
            s.Weather.SilentNight.Count = 10;
            int before = steam.UnlockCalls;
            AchievementService.Evaluate(s);
            Expect(U("silent_nights_ten") && AchievementService.Entry("silent_nights_ten").Synced && steam.UnlockCalls > before && steam.Achievements.Contains("SILENT_NIGHTS_TEN"),
                "G2 在线时解锁当场推到平台（SetAchievement 一次），账本标已同步");

            // G3 负向：账本主文件损坏 → 改读 .bak（离线未同步的不丢）；主文件和 .bak 都坏 → 按空账本继续，但本次运行不写回、不覆盖坏文件。
            FreshLedger("ledger-bad");
            CampaignState g = Small("fgach-ledger-bad");
            g.Research.Fusion.Discovered = new[] { "r0" };
            AchievementService.Evaluate(g); // 离线：fusion_first 等待同步
            VibrationService.StateOf(g).Kills = 1;
            AchievementService.Evaluate(g); // 第二次写：旧主档转存为 .bak（只含 fusion_first）
            string main = AchievementService.FilePath;
            bool hasBak = File.Exists(main + ".bak");
            File.WriteAllText(main, "{ 坏掉的账本");
            AchievementService.ResetForTests();
            bool fromBak = U("fusion_first") && AchievementService.Entry("fusion_first") is { Synced: false };
            File.WriteAllText(main, "{ 坏掉的账本");
            File.WriteAllText(main + ".bak", "也坏了");
            AchievementService.ResetForTests();
            bool emptyRun = !U("fusion_first");
            g.Research.Analysis ??= new AnalysisBenchState();
            g.Research.Analysis.Completed = 20;
            AchievementService.Evaluate(g); // 本次运行内照样算解锁，但不写回
            bool notOverwritten = U("crack_twenty") && File.ReadAllText(main) == "{ 坏掉的账本" && File.ReadAllText(main + ".bak") == "也坏了";
            Expect(hasBak && fromBak && emptyRun && notOverwritten,
                $"G3 负向：成就账本主文件损坏 → 读 .bak 找回离线未同步的解锁（{fromBak}）；主文件和备份都坏 → 本次按空账本继续、照常解锁，但不覆盖坏文件（{notOverwritten}）");
        }

        // ─────────────────────────────── H 云存档与 Steam 提供者 ───────────────────────────────

        private static void CheckCloudAndSteam()
        {
            FreshLedger("steam");
            CloudSaveService.Provider = null;
            AchievementService.Provider = null;
            PlatformServices.Init(); // 正式启动路径：没有 Steamworks = 离线
            CampaignState s = Small("fgach-steam");
            s.Research.Fusion.Discovered = new[] { "r0" };
            AchievementService.Evaluate(s);
            bool offline = SteamPlatform.TryCreate() == null && SteamPlatform.TryCreateCloud() == null && AchievementService.Provider == null && !CloudSaveService.Available
                           && !CloudSaveService.Sync().Ran && U("fusion_first") && AchievementService.PendingSyncCount == 1;
            Expect(offline, "H1 Steam 提供者（DEBT-FG15SYS01-02 用例）：工程未接入 Steamworks 时启动 = 离线，云存档不对账、游戏照常；成就照常在本机解锁、等待同步");

            // 云存档冲突：账本在存档目录里但不是战役槽，不上云、不被覆盖；选“保留云端”后成就一条不少。
            AchievementService.FilePathOverrideForTests = Path.Combine(_dir, "achievements.json");
            AchievementService.ResetForTests();
            AchievementService.Evaluate(s);
            int before = AchievementService.UnlockedTotal;
            string cloudDir = Path.Combine(_dir, "_cloud");
            CloudSaveService.Provider = new LocalFolderCloudProvider(cloudDir);
            CampaignState local = Small("fgach-cloud");
            local.Scrap = 1;
            CampaignSaveService.Save(0, local, SaveReason.NewCampaign);
            CloudSaveService.Sync();
            local.Scrap = 2;
            CampaignSaveService.Save(0, local, SaveReason.Manual);
            string other = Path.Combine(_dir, "_other");
            CampaignSaveService.SaveDirectoryOverrideForTests = other;
            CampaignState remote = Small("fgach-cloud");
            remote.Scrap = 3;
            CampaignSaveService.Save(0, remote, SaveReason.Manual);
            File.Copy(CampaignSaveService.SlotPath(0), Path.Combine(cloudDir, "campaign_slot0.json"), true);
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            CloudSyncReport clash = CloudSaveService.Sync();
            CloudSaveConflict c = CloudSaveService.Conflicts.FirstOrDefault();
            bool resolved = c != null && CloudSaveService.Resolve(c, keepLocal: false);
            CampaignState kept = CampaignSaveService.Load(0).State;
            AchievementService.ResetForTests();
            if (kept != null)
            {
                AchievementService.Evaluate(kept);
            }
            bool notUploaded = !File.Exists(Path.Combine(cloudDir, "achievements.json")) && !CloudSaveService.IsSyncedName("achievements.json");
            Expect(clash.Conflicts == 1 && resolved && kept?.Scrap == 3 && AchievementService.UnlockedTotal == before && U("fusion_first") && notUploaded,
                $"H2 云存档冲突选“保留云端”（另一份另存）：成就账本独立于战役存档、不上云、不被覆盖，冲突前后解锁数 {before} → {AchievementService.UnlockedTotal}，读回云端那份再判也不会撤销");
            CloudSaveService.Provider = null;
        }

        // ─────────────────────────────── I 富状态与覆盖层 ───────────────────────────────

        private static void CheckPresenceAndOverlay()
        {
            var steam = new LocalPlatformProvider();
            FreshLedger("presence", steam);
            CampaignState s = Small("fgach-presence");
            s.CurrentRegionId = HomeValleyLayout.RegionId;
            PlatformServices.PushPresence(null, false);
            string menu = steam.Presence.TryGetValue("status", out string m0) ? m0 : null;
            PlatformServices.PushPresence(s, true);
            string act1 = steam.Presence["status"];
            int pushes = PlatformServices.PresencePushes;
            PlatformServices.PushPresence(s, true);
            bool once = PlatformServices.PresencePushes == pushes;
            s.Progress.Act = 2;
            PlatformServices.PushPresence(s, true);
            string act2 = steam.Presence["status"];
            s.Progress.IsSandbox = true;
            PlatformServices.PushPresence(s, true);
            string sbx = steam.Presence["status"];
            string place = CampaignSlotText.RegionName(HomeValleyLayout.RegionId);
            Expect(menu == "主菜单" && act1 == $"第 1 幕 · {place}" && once && act2.StartsWith("第 2 幕", StringComparison.Ordinal) && steam.Presence["act"] == "2"
                   && steam.Presence["region"] == place && sbx.StartsWith("沙盒", StringComparison.Ordinal) && !GameText.ContainsMarker(act1 + sbx),
                $"I1 富状态（FGR-SYS-080 显示当前幕和区域）：“{menu}”→“{act1}”→“{act2}”→“{sbx}”；内容不变不重复推送（{once}）");

            bool paused = false;
            FocusPolicy.ResetForTests();
            FocusPolicy.WorldRunning = () => true;
            FocusPolicy.IsPaused = () => paused;
            FocusPolicy.SetPaused = p => paused = p;
            GameSettings.SetPauseOnFocusLoss(true);
            steam.RaiseOverlay(true);
            FocusPolicy.Tick(true && !PlatformServices.OverlayActive);
            bool on = paused && PlatformServices.OverlayActive;
            steam.RaiseOverlay(false);
            FocusPolicy.Tick(true && !PlatformServices.OverlayActive);
            Expect(on && !paused, "I2 覆盖层兼容：Steam 覆盖层打开按失焦处理（失焦暂停开着时暂停），关掉恢复（只恢复它自己停的）");
            FocusPolicy.ResetForTests();
        }

        // ─────────────────────────────── J 存读档 ───────────────────────────────

        private static void CheckSaveLoad()
        {
            FreshLedger("save");
            CampaignState s = Small("fgach-save");
            AchievementService.Evaluate(s);
            AchievementCampaignState st = s.Achievements;
            st.ReverseHijacks = 7;
            st.RaidsRepelledAway = 1;
            st.LineMinutes = 12;
            st.LinePlanStep = 4;
            st.Act1Losses = 2;
            st.Hooks = new[] { GuidanceHooks.LogisticsFirstBelt };
            st.NextEvalTick = 3600;
            SaveResult r = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            CampaignState back = CampaignSaveService.Load(Slot).State;
            AchievementCampaignState b = back?.Achievements;
            Expect(r.Success && b != null && b.Initialized && b.Act1Tracked && b.ReverseHijacks == 7 && b.RaidsRepelledAway == 1 && b.LineMinutes == 12 && b.LinePlanStep == 4
                   && b.Act1Losses == 2 && b.Hooks.SequenceEqual(st.Hooks) && b.NextEvalTick == 3600 && CampaignFgStateDomains.All.Any(d => d.FieldName == nameof(CampaignState.Achievements)),
                "J1 成就域进存档并逐项读回（反向接入、远征期间击退、产线分钟与规划步号、第一幕阵亡、钩子、下一次判定步），登记在存档域清单里");
            AchievementService.Evaluate(back);
            Expect(U("first_belt"), "J2 读回的钩子记录照样解锁“第一条传送带”（本局放过传送带，钩子“第一次”是跨战役记录，不能当本局事实）");
        }

        // ─────────────────────────────── K 面板 ───────────────────────────────

        private static void CheckPanel()
        {
            FreshLedger("panel");
            CampaignState s = Small("fgach-panel");
            s.Progress.IsSandbox = true;
            s.Research.Fusion.Discovered = new[] { "r0", "r1", "r2" };
            AchievementService.Evaluate(s);
            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PanelUxml).CloneTree();
            var go = new GameObject("__FgAchievementPanel") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                AchievementPanelUIToolkit panel = go.AddComponent<AchievementPanelUIToolkit>();
                panel.BindView(tree);
                AchievementPanelUIToolkit.Open();
                int idx = Enumerable.Range(0, panel.RowCount).FirstOrDefault(i => panel.RowName(i).StartsWith("第一次熔合", StringComparison.Ordinal));
                bool rows = AchievementPanelUIToolkit.IsOpen && panel.RowCount == AchievementService.Rows.Count && panel.RowUnlocked(idx) && panel.RowIconClasses(idx).Contains("ach-shape-diamond")
                            && panel.StatusText == $"已解锁 1 / {AchievementService.Rows.Count}" && panel.SyncText.Contains("离线") && panel.SandboxNoteText.Contains("沙盒存档不计入剧情类成就");
                string locked = AchievementPanelUIToolkit.StateLine(AchievementService.Find("fusion_ten"), s, false);
                string story = AchievementPanelUIToolkit.StateLine(AchievementService.Find("vanguard_repelled"), s, false);
                string menu = AchievementPanelUIToolkit.StateLine(AchievementService.Find("fusion_ten"), null, false);
                bool states = locked == "进度 3 / 10" && story == "沙盒存档不计入" && menu == "未解锁" && panel.RowState(idx).Contains("等待同步");
                bool text = !GameText.ContainsMarker(panel.StatusText + panel.SyncText + panel.SandboxNoteText + string.Join("", Enumerable.Range(0, panel.RowCount).Select(i => panel.RowName(i) + panel.RowState(i))));
                panel.SetOpen(false);
                Expect(rows && states && text,
                    $"K1 成就面板：{panel.RowCount} 条按分类列出、几何占位图标、“{panel.StatusText}”、“{panel.SyncText}”；状态行“{locked}”/“{story}”/主菜单“{menu}”；沙盒规则说明再写一次（FGT-SBX-005）");
            }
            finally
            {
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
            // K3 入口可达：真暂停菜单 UXML 里的“成就”按钮（与真实点击同一个 Clickable 回调）打开成就面板。主菜单“成就”按钮是 uGUI 预制体上复制出来的，由 PlaySmokeTest 主菜单一步覆盖（交 E2E 冒烟）。
            GameObject pgo = null, ago = null;
            try
            {
                VisualElement aroot = FgProductionSelfCheck.MountUxml(PanelUxml, out ago);
                ago.AddComponent<AchievementPanelUIToolkit>().BindView(aroot);
                VisualElement proot = FgProductionSelfCheck.MountUxml("Assets/GameRes/Raw/UI/UiKit/PauseMenu.uxml", out pgo);
                PauseMenuUIToolkit pm = pgo.AddComponent<PauseMenuUIToolkit>();
                pm.BindView(proot);
                bool closedBefore = !AchievementPanelUIToolkit.IsOpen;
                bool clicked = ClickButton(pm.AchievementsButton);
                Expect(closedBefore && pm.AchievementsButton != null && pm.AchievementsButton.text.Contains("成就") && clicked && AchievementPanelUIToolkit.IsOpen,
                    $"K3 暂停菜单“{pm.AchievementsButton?.text}”按钮（PauseAchievements）点一下打开成就面板");
                AchievementPanelUIToolkit.Close();
            }
            finally
            {
                UiEscapeStack.Clear();
                if (pgo != null)
                {
                    Object.DestroyImmediate(pgo);
                }
                if (ago != null)
                {
                    Object.DestroyImmediate(ago);
                }
            }

            string probe = BinGames.EditorTools.UiToolkitLayoutProbe.Probe(PanelUxml, "AchievementWindow", stressFill: true,
                prepare: rr => rr.panel.visualTree.Q<VisualElement>("AchievementRoot")?.RemoveFromClassList("uk-hidden"));
            Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "K2 成就面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static CampaignState NewWorld(int seed)
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
            CampaignState s = CampaignState.CreateNew("fgach-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>与真实点击同一个回调（Clickable.Invoke）；按钮为空 / 没有 Clickable / 被禁用返回 false（同 FgAwayReportSelfCheck.Click）。</summary>
        private static bool ClickButton(Button b)
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

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
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

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
