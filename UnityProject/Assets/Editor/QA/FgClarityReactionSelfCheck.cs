using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
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
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG10-REACT-01 化工反应开放（FG02 FGR-FW-042；FGT-FW-003 扩展；卡片“以探针结果为准”“图鉴写明触发方式”“首次触发的慢放”、负向“探针显示某条不可触发：登记缺口”）：
    /// A 数据：化工批 = 爆燃 / 腐蚀灼烧 / 胶结 / 溶蚀（设计名），四条都标可触发；每条有打法提示；未开放时图鉴写“澄净领地出现时开放”；通知类型。
    /// B 可得性探针（内容层）：每条反应的两个配料，到第二幕为止玩家都拿得到（基础 / 遗迹 / 第一幕阵营 / 澄净掉落的固件，或澄净敌人自带）——拿不到的条目必须已登记缺口。
    ///   正式战斗内核里“装上真实固件就能触发 / 不触发”的对照在 FgReactionProbeSelfCheck E（FGT-FW-003）。
    /// C 内核负向：澄净单位不怕酸（免疫酸蚀位）→ 腐蚀灼烧 / 溶蚀在它身上打不出来，换成普通敌人就能；爆燃（油 + 火）在它身上照样打得出来。
    /// D 正式开放（D2 覆盖种子测试集 1 / 42 / 20261017）：第一幕不开放；澄净领地出现在地图上（第二幕）的下一个游戏秒开放并通知一次（列四个名字），之后不重复；开放前不报名字、开放后报名字；图鉴提示随之变化。
    /// E 首次触发：开放前打出爆燃不慢放、不算首次；开放后第一次打出 → 慢放一次 + 首次记录（图鉴解锁），第二次不再慢放。
    /// F 计时与存档：正式世界步进（WorldSimulation.Frame）下暂停不开放，0.5x / 1x / 2x / 3x 都在澄净领地出现后的第一个整游戏秒开放一次；真实存档 → 读档后批次仍开放、不重发；旧档（没有开放字段）进第二幕后第一个整游戏秒补开放。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgClarityReactionSelfCheck）。
    /// </summary>
    public static class FgClarityReactionSelfCheck
    {
        private const int Slot = 7;
        private const string SiteId = "fg10react01-site";
        private const string Batch = "clarity";
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static readonly Dictionary<string, string> SpecNames = new Dictionary<string, string>
        {
            ["reaction_deflagrate"] = "爆燃", ["reaction_causticburn"] = "腐蚀灼烧", ["reaction_syrup"] = "胶结", ["reaction_dissolve"] = "溶蚀",
        };

        private static StringBuilder _report;
        private static string _dir;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/FG 化工反应开放")]
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
            Line("\n[化工反应开放] 爆燃 / 腐蚀灼烧 / 胶结 / 溶蚀：可得性探针、澄净不怕酸的负向、正式开放时机、图鉴打法、首次触发慢放（FG10-REACT-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            Func<string> originalObserved = FeedbackCues.ObservedSiteProvider;
            bool slowmo = GameSettings.ReactionSlowMotionEnabled;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgreact-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                NotificationCenter.AutoPauseHandler = null;
                ReactionBatchService.ResetForTests();

                Step(CheckData);
                Step(CheckObtainable);
                Step(CheckClarityAcidProof);
                Step(CheckOpening);
                Step(CheckFirstTrigger);

                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                GameRoot.BindWorldProviders();
                Step(CheckTimingAndSave);
            }
            finally
            {
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                CameraDirector.RealDeltaTime = originalDelta;
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
                FeedbackCues.ObservedSiteProvider = originalObserved;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GameSettings.SetReactionSlowMotionEnabled(slowmo);
                GameClock.CancelSlowMotion();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
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
            }
            Line(_fail == 0 ? $"  化工反应开放：全部 {_pass} 项通过" : $"  化工反应开放：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        private static IEnumerable<Reaction> ClarityRows() => NamedReactionCatalog.Rows.Where(r => r != null && r.Batch == Batch);

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            Reaction[] rows = ClarityRows().ToArray();
            bool names = rows.Length == SpecNames.Count && rows.All(r => SpecNames.TryGetValue(r.Id, out string n) && GameText.Get(r.NameKey) == n);
            bool reach = rows.All(r => r.Reach == NamedReactionCatalog.Reachable);
            string[] noTip = rows.Where(r => string.IsNullOrEmpty(ReactionBatchService.TipOf(r))).Select(r => r.Id).ToArray();
            bool closedHint = ReactionBatchService.ClosedHint(Batch) == GameText.Get("reaction.codex.closed_clarity");
            bool notify = NotificationCatalog.TryGetType(ReactionBatchService.NotifyType, out _) && GameText.Has("reaction.batch.opened");
            Expect(names && reach && noTip.Length == 0 && closedHint && notify,
                $"A1 化工批 = {string.Join(" / ", rows.Select(r => GameText.Get(r.NameKey)))}（设计名一致 {names}），全部标可触发（{reach}）；每条有打法提示（缺 {string.Join(",", noTip)}）；" +
                $"未开放时图鉴写“{ReactionBatchService.ClosedHint(Batch)}”；开放通知类型与文本（{notify}）");
        }

        // ─────────────────────────────── B 可得性探针 ───────────────────────────────

        /// <summary>到第二幕为止玩家拿得到的固件：基础 / 终端 / 缓存 / 遗迹；静默、铸造、澄净的残骸 / 精英 / 首领（澄净首领除外）。熔合产物不算（另有研究门槛）。</summary>
        private static bool ObtainableByAct2(GameConfig.fg.FirmwareKind fw)
        {
            if (fw == null || (!string.IsNullOrEmpty(fw.MixA) && fw.MixA != "none"))
            {
                return false;
            }
            switch (fw.Source)
            {
                case "base":
                case "terminal":
                case "cache":
                case "relic":
                    return true;
                case "salvage":
                case "elite":
                    return fw.Faction == "silent" || fw.Faction == "foundry" || fw.Faction == "clarity";
                case "boss":
                    return fw.Faction == "silent" || fw.Faction == "foundry";
                default:
                    return false;
            }
        }

        private static bool HasTag(GameConfig.fg.FirmwareKind fw, string tag) =>
            fw?.Tags != null && fw.Tags.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(t => StatusTagCatalog.Canonical(t) == StatusTagCatalog.Canonical(tag));

        private static void CheckObtainable()
        {
            string gapText = File.ReadAllText(Path.Combine(GameLogic.EditorTools.JourneyBots.JourneyCheckpoints.RepoRoot() ?? string.Empty, "production", "design", "full-game", "FG-GAP-REGISTER.md"));
            var lines = new List<string>();
            var bad = new List<string>();
            // 澄净敌人自带的配料（不用玩家装固件）：表里 statusTag 列 + 读法（爬行者一路带油）。
            var enemyTags = new HashSet<string>(EnemyDefs.Rows.Where(e => e != null && e.Faction == EnemyDefs.FactionClarity && !string.IsNullOrEmpty(e.StatusTag) && e.StatusTag != "none")
                .Select(e => StatusTagCatalog.Canonical(e.StatusTag)));
            foreach (Reaction r in ClarityRows())
            {
                string Source(string tag)
                {
                    GameConfig.fg.FirmwareKind fw = FirmwareKinds.Rows.Where(f => HasTag(f, tag) && ObtainableByAct2(f)).OrderBy(f => f.Source == "base" ? 0 : 1).ThenBy(f => f.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (fw != null)
                    {
                        return FirmwareKinds.DisplayName(fw.Id) + "（" + fw.Source + (fw.Faction != null && fw.Faction != "none" ? "·" + fw.Faction : string.Empty) + "）";
                    }
                    return enemyTags.Contains(StatusTagCatalog.Canonical(tag)) ? "澄净敌人自带" : null;
                }
                string a = Source(r.TagA);
                string b = Source(r.TagB);
                if (a == null || b == null)
                {
                    // 负向（卡片）：探针显示某条不可触发 → 必须登记缺口并在表里标 unreachable。
                    if (r.Reach == NamedReactionCatalog.Reachable || r.Gap == "none" || !gapText.Contains("| " + r.Gap + " |"))
                    {
                        bad.Add($"{GameText.Get(r.NameKey)} 第二幕前拿不到配料（{r.TagA}:{a ?? "无"} / {r.TagB}:{b ?? "无"}），但没有标不可触发并登记缺口");
                    }
                    continue;
                }
                lines.Add($"{GameText.Get(r.NameKey)} = {r.TagA}[{a}] + {r.TagB}[{b}]");
            }
            Expect(bad.Count == 0 && lines.Count == SpecNames.Count,
                $"B1 可得性探针：{string.Join("；", lines)}{(bad.Count > 0 ? " ✗ " + string.Join("；", bad) : string.Empty)}");
        }

        // ─────────────────────────────── C 澄净不怕酸 ───────────────────────────────

        private static void CheckClarityAcidProof()
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using (var k = new CombatKernel(cfg, 32))
            {
                k.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
                k.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
                uint acid = NamedReactionCatalog.BitOf("Acid");
                uint oil = NamedReactionCatalog.BitOf("Oil");
                uint fire = NamedReactionCatalog.BitOf("Fire");
                int proof = k.AddProfile(new CombatBehaviorProfile { ImmuneMask = acid, AcidResist = 1f });
                float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
                int Count(string id)
                {
                    IReadOnlyList<Reaction> rules = NamedReactionCatalog.TagRules;
                    for (int i = 0; i < rules.Count; i++)
                    {
                        if (rules[i].Id == id)
                        {
                            return k.ReactionCountOf(i);
                        }
                    }
                    return -1;
                }
                int Probe(int profile, double y, uint first, uint second, float secondDps, string id, float hit = 0f)
                {
                    int before = Count(id);
                    int e = k.Spawn(Hostile(new double2(3, y), profile));
                    k.ApplyStatus(e, first, 5f, 0f, 0f, 0f, 0);
                    k.ApplyStatus(e, second, 3f, secondDps, 0f, 0f, 0, hit);
                    k.Step(1f / 60f, k.Time + 1f / 60f);
                    k.ClearCues();
                    k.DrainGameplay(4096, out _);
                    return Count(id) - before;
                }
                int burnPlain = Probe(-1, 0, acid, fire, fireDps, "reaction_causticburn");
                int burnProof = Probe(proof, 1, acid, fire, fireDps, "reaction_causticburn");
                // 溶蚀只有“这一击 × 倍率”（没有附加标签 / 残留区域）：纯挂状态不结算（内核规则），要用一记带油的命中（如爬行者自爆）。
                int etchPlain = Probe(-1, 2, acid, oil, 0f, "reaction_dissolve", 10f);
                int etchProof = Probe(proof, 3, acid, oil, 0f, "reaction_dissolve", 10f);
                int deflProof = Probe(proof, 4, oil, fire, fireDps, "reaction_deflagrate");
                Expect(burnPlain == 1 && burnProof == 0 && etchPlain == 1 && etchProof == 0 && deflProof == 1,
                    $"C1 澄净单位不怕酸（免疫酸蚀位）：腐蚀灼烧 普通敌人 {burnPlain} 次 / 澄净 {burnProof} 次；溶蚀 普通 {etchPlain} / 澄净 {etchProof}；爆燃在澄净身上照样 {deflProof} 次——图鉴打法提示写明“这两条要打在其它阵营身上”");
            }
        }

        private static CombatSpawn Hostile(double2 at, int profile) => new CombatSpawn
        {
            Kind = CombatUnitKind.Enemy,
            Faction = CombatFaction.Hostile,
            Behavior = CombatBehavior.HoldFire,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = at,
            Home = at,
            Radius = 0.4f,
            Speed = 0f,
            Health = 1e6f,
            MaxHealth = 1e6f,
            Weapon = -1,
            BehaviorProfile = profile,
            Priority = 1,
        };

        // ─────────────────────────────── D 正式开放 ───────────────────────────────

        private static void CheckOpening()
        {
            CampaignState s = NewWorldState(42);
            PlannedTerritory lake = WorldGenService.PlanFor(s)?.Find(EnemyDefs.FactionClarity);
            Reaction burn = ClarityRows().First(r => r.Id == "reaction_causticburn");
            MechanicCodexEntry entry = MechanicCodex.Find(MechanicCodex.ReactionEntryId(burn.Id));
            long tick = GameClock.StepHz * 10L;
            int opened0 = ReactionBatchService.Opened;
            ReactionBatchService.WorldStep(s, tick);
            bool closedAct1 = !NamedReactionCatalog.IsBatchOpen(s, Batch) && !NamedReactionCatalog.IsNamed(s, burn.Id) && lake != null && !HomeExplorationService.TerritoryVisible(s, lake)
                              && MechanicCodex.Hint(entry, s) == ReactionBatchService.ClosedHint(Batch);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act); // 第二幕：澄净领地在地图上显示
            }
            int notes0 = NotificationCenter.History.Count(n => n.Type != null && n.Type.Id == ReactionBatchService.NotifyType);
            ReactionBatchService.WorldStep(s, tick + 1); // 不是整游戏秒：这一步不查
            bool waitsForSecond = !NamedReactionCatalog.IsBatchOpen(s, Batch);
            ReactionBatchService.WorldStep(s, tick + GameClock.StepHz);
            bool open = NamedReactionCatalog.IsBatchOpen(s, Batch) && ReactionBatchService.Opened == opened0 + 1 && ClarityRows().All(r => NamedReactionCatalog.IsNamed(s, r.Id));
            NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == ReactionBatchService.NotifyType);
            string text = note?.Latest?.DetailText ?? note?.Text ?? string.Empty;
            bool listed = SpecNames.Values.All(text.Contains);
            ReactionBatchService.WorldStep(s, tick + GameClock.StepHz * 2);
            int notes1 = NotificationCenter.History.Count(n => n.Type != null && n.Type.Id == ReactionBatchService.NotifyType);
            bool once = ReactionBatchService.Opened == opened0 + 1;
            bool hintOpen = MechanicCodex.Hint(entry, s) == GameText.Get("reaction.codex.locked");
            bool tipInBody = MechanicCodex.Body(entry, s).Contains(ReactionBatchService.TipOf(burn));
            Expect(closedAct1 && waitsForSecond && open && listed && once && notes1 >= notes0 + 1 && hintOpen && tipInBody,
                $"D1 第一幕不开放（图鉴写“{ReactionBatchService.ClosedHint(Batch)}”）；澄净领地出现在地图上后的整游戏秒开放化工批并通知一次（“{text}”），之后不重复（{once}）；" +
                $"开放后四条都报名字，图鉴提示变成“{GameText.Get("reaction.codex.locked")}”，正文含打法提示（{tipInBody}）");

            // D2 种子无关（B25）：种子测试集里每个世界都有澄净领地，第一幕不开放、第二幕的整游戏秒开放。
            var seeds = new List<string>();
            bool allSeeds = true;
            foreach (int seed in new[] { 1, 42, 20261017 })
            {
                CampaignState w = NewWorldState(seed);
                bool hasLake = WorldGenService.PlanFor(w)?.Find(EnemyDefs.FactionClarity) != null;
                ReactionBatchService.WorldStep(w, tick);
                bool shut = !NamedReactionCatalog.IsBatchOpen(w, Batch);
                if (w.Progress != null)
                {
                    w.Progress.Act = Math.Max(2, w.Progress.Act);
                }
                ReactionBatchService.WorldStep(w, tick + GameClock.StepHz);
                bool ok = hasLake && shut && NamedReactionCatalog.IsBatchOpen(w, Batch);
                allSeeds &= ok;
                seeds.Add($"{seed}:{ok}");
            }
            Expect(allSeeds, $"D2 种子测试集（{string.Join(" ", seeds)}）：都有澄净领地，第一幕不开放、第二幕开放");
        }

        // ─────────────────────────────── E 首次触发慢放 ───────────────────────────────

        private static void CheckFirstTrigger()
        {
            CampaignState s = NewState(9420);
            FeedbackCues.ObservedSiteProvider = () => SiteId;
            GameSettings.SetReactionSlowMotionEnabled(true);
            GameClock.CancelSlowMotion();
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using (var site = new CombatSite(SiteId, cfg))
            {
                float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
                void Deflagrate(double y)
                {
                    int e = site.Kernel.Spawn(Hostile(new double2(3, y), -1));
                    site.Step(1f / 60f, site.Kernel.Time + 1f / 60f);
                    site.Kernel.ApplyStatus(e, NamedReactionCatalog.BitOf("Oil"), 5f, 0f, 0f, 0f, 0);
                    site.Kernel.ApplyStatus(e, NamedReactionCatalog.BitOf("Fire"), 3f, fireDps, 0f, 0f, 0);
                    site.Step(1f / 60f, site.Kernel.Time + 1f / 60f);
                }
                int slow0 = GameClock.SlowMotionStarts;
                Deflagrate(0);
                bool closedQuiet = GameClock.SlowMotionStarts == slow0 && !ReactionFeedback.IsFirstTriggered(s, "reaction_deflagrate");
                ReactionBatchService.Open(s, Batch);
                Deflagrate(2);
                bool firstSlow = GameClock.SlowMotionStarts == slow0 + 1 && ReactionFeedback.IsFirstTriggered(s, "reaction_deflagrate");
                GameClock.CancelSlowMotion();
                Deflagrate(4);
                bool noSecond = GameClock.SlowMotionStarts == slow0 + 1 && s.ReactionFirstTriggers.Count(r => r.ReactionId == "reaction_deflagrate") == 1;
                Expect(closedQuiet && firstSlow && noSecond,
                    $"E1 首次触发（FGR-FW-043）：开放前打出爆燃不慢放、不算首次（{closedQuiet}）；开放后第一次 → 慢放一次 + 首次记录（图鉴解锁）（{firstSlow}）；第二次不再慢放（{noSecond}）");
            }
            GameClock.CancelSlowMotion();
        }

        // ─────────────────────────────── F 计时与存档 ───────────────────────────────

        private static void CheckTimingAndSave()
        {
            var lines = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState w = NewHomeWorld(42);
                if (w.Progress != null)
                {
                    w.Progress.Act = Math.Max(2, w.Progress.Act); // 澄净领地在地图上显示
                }
                int opened0 = ReactionBatchService.Opened;
                GameClock.SetPaused(true);
                for (int i = 0; i < 30; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool pausedShut = !NamedReactionCatalog.IsBatchOpen(w, Batch) && ReactionBatchService.Opened == opened0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                long visibleAt = GameClock.Ticks;
                long deadline = (visibleAt / GameClock.StepHz + 1) * GameClock.StepHz; // 下一个整游戏秒
                long openedAt = -1;
                for (int i = 0; i < 4000 && GameClock.Ticks < deadline + GameClock.StepHz * 2; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                    if (openedAt < 0 && NamedReactionCatalog.IsBatchOpen(w, Batch))
                    {
                        openedAt = GameClock.Ticks;
                    }
                }
                GameClock.SetSpeed(1f);
                // 一帧可能推进多步：记下的是开放那一帧结束时的步号，应落在整秒那一步到整秒后一秒之间。
                bool onTime = openedAt >= deadline && openedAt < deadline + GameClock.StepHz;
                bool once = ReactionBatchService.Opened == opened0 + 1;
                ok &= pausedShut && onTime && once;
                lines.Add($"{speed}x 暂停不开放 {pausedShut} / 第 {openedAt - visibleAt} 步开放（整秒在第 {deadline - visibleAt} 步）/ 只开放一次 {once}");
            }
            Expect(ok, $"F1 正式世界步进：{string.Join("；", lines)}");

            // F2 真实存档 → 读档（最后一档的世界：化工批已开放）。
            CampaignState s = CampaignSession.Current;
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!sv.Success || !rr.Success)
            {
                Fail($"F2 准备：存档 {sv.Success} / 读档 {rr.Success}（{rr.Message}）");
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            CampaignState back = rr.State;
            HomeValleyController hv = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(hv.SiteId);
            int openedBack = ReactionBatchService.Opened;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool kept = NamedReactionCatalog.IsBatchOpen(back, Batch) && ClarityRows().All(r => NamedReactionCatalog.IsNamed(back, r.Id)) && ReactionBatchService.Opened == openedBack;

            // 旧档没有开放字段：读进来是未开放；进第二幕后第一个整游戏秒补开放。
            CampaignState legacy = JsonUtility.FromJson<CampaignState>(SaveMigrationJson.RemoveField(JsonUtility.ToJson(back), "OpenReactionBatches"));
            CampaignFgStateDomains.EnsureAll(legacy);
            bool legacyShut = legacy != null && !NamedReactionCatalog.IsBatchOpen(legacy, Batch);
            ReactionBatchService.WorldStep(legacy, GameClock.StepHz * 1000L);
            bool legacyOpen = legacy != null && NamedReactionCatalog.IsBatchOpen(legacy, Batch);
            Expect(s != null && kept && legacyShut && legacyOpen,
                $"F2 存读档：读档后化工批仍开放、四条都报名字，再走 2 个游戏秒不重发（{kept}）；旧档无字段 → 未开放（{legacyShut}）→ 第二幕第一个整游戏秒补开放（{legacyOpen}）");
        }

        /// <summary>带家园的正式世界（与 FgSettlingLakeSelfCheck 同一夹具，去掉信号接入部分）。</summary>
        private static CampaignState NewHomeWorld(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgreact-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            NotificationCenter.Bind(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static CampaignState NewState(int seed)
        {
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("fgreact-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            CampaignFgStateDomains.EnsureAll(s);
            return s;
        }

        private static CampaignState NewWorldState(int seed)
        {
            GameClock.ResetSession();
            WorldGenService.Invalidate();
            HomeGridService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fgreact-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            NotificationCenter.Bind(s); // 与正式流程一样换战役即绑定：否则 History 还是前一段自检那局的通知（如沉淀湖 E1 已开放化工批），计数基线会错
            return s;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgClarityReactionSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
