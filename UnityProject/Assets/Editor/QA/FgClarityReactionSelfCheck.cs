using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
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
    /// D 正式开放：第一幕不开放；澄净领地出现在地图上（第二幕）的下一个游戏秒开放并通知一次（列四个名字），之后不重复；开放前不报名字、开放后报名字；图鉴提示随之变化。
    /// E 首次触发：开放前打出爆燃不慢放、不算首次；开放后第一次打出 → 慢放一次 + 首次记录（图鉴解锁），第二次不再慢放。
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
            }
            finally
            {
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
            string gapText = File.ReadAllText(Path.Combine(Application.dataPath, "..", "..", "..", "production", "design", "full-game", "FG-GAP-REGISTER.md"));
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
                int Probe(int profile, double y, uint first, uint second, float secondDps, string id)
                {
                    int before = Count(id);
                    int e = k.Spawn(Hostile(new double2(3, y), profile));
                    k.ApplyStatus(e, first, 5f, 0f, 0f, 0f, 0);
                    k.ApplyStatus(e, second, 3f, secondDps, 0f, 0f, 0);
                    k.Step(1f / 60f, k.Time + 1f / 60f);
                    k.ClearCues();
                    k.DrainGameplay(4096, out _);
                    return Count(id) - before;
                }
                int burnPlain = Probe(-1, 0, acid, fire, fireDps, "reaction_causticburn");
                int burnProof = Probe(proof, 1, acid, fire, fireDps, "reaction_causticburn");
                int etchPlain = Probe(-1, 2, acid, oil, 0f, "reaction_dissolve");
                int etchProof = Probe(proof, 3, acid, oil, 0f, "reaction_dissolve");
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
