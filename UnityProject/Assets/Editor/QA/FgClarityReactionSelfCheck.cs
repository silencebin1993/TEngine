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
            ["reaction_deflagrate"] = "爆燃", ["reaction_causticburn"] = "腐蚀灼烧", ["reaction_syrup"] = "胶结", ["reaction_dissolve"] = "乳化",
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
                ReactionDiscoveryService.ResetForTests();

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
            string[] noTip = rows.Where(r => string.IsNullOrEmpty(ReactionDiscoveryService.TipOf(r))).Select(r => r.Id).ToArray();
            bool notify = NotificationCatalog.TryGetType(ReactionDiscoveryService.NotifyType, out NotifyTypeDef notifyDef) && notifyDef.Tier == NotifyLevel.Info && GameText.Has("reaction.discovery.named");
            Expect(names && reach && noTip.Length == 0 && notify,
                $"A1 化工批 = {string.Join(" / ", rows.Select(r => GameText.Get(r.NameKey)))}（设计名一致 {names}），全部标可触发（{reach}）；每条有打法提示（缺 {string.Join(",", noTip)}）；“新发现”通知信息级、文本在（{notify}）");
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
                CombatSite.ApplyContent(k); // EMG2-REACT-01：爆燃改走沾染行 C01，与地点同一套登记
                uint acid = NamedReactionCatalog.BitOf("Acid");
                uint oil = NamedReactionCatalog.BitOf("Oil");
                uint fire = NamedReactionCatalog.BitOf("Fire");
                int proof = k.AddProfile(new CombatBehaviorProfile { ImmuneMask = acid, AcidResist = 1f });
                float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
                int Count(string id) => CombatSite.ReactionCountOf(k, id); // EMG2-REACT-01：爆燃已端口化，按稳定键读涌现计数
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
                // EMG2-REACT-02（FG19B M03）：乳化是斑块层的“油 × 酸 → 污泥”，单位身上的两种沾染不结算；澄净单位不沾酸也就打不到（ADR-REACT-001 第 5 节待复核项的新口径）
                int etchPlain = Probe(-1, 2, acid, oil, 0f, "reaction_dissolve", 10f);
                int etchProof = Probe(proof, 3, acid, oil, 0f, "reaction_dissolve", 10f);
                int etch0 = Count("reaction_dissolve");
                k.SpawnPatch(EmgMatterConst.Oil, new double2(40, 0), 2f, 3f, 30f);
                k.SpawnPatch(EmgMatterConst.Acid, new double2(41, 0), 2f, 3f, 30f);
                for (int s = 0; s < 60; s++)
                {
                    k.Step(1f / 60f, k.Time + 1f / 60f);
                }
                int etchPatch = Count("reaction_dissolve") - etch0;
                int deflProof = Probe(proof, 4, oil, fire, fireDps, "reaction_deflagrate");
                Expect(burnPlain == 1 && burnProof == 0 && etchPlain == 0 && etchProof == 0 && etchPatch >= 1 && deflProof == 1,
                    $"C1 澄净单位不怕酸（免疫酸蚀位）：腐蚀灼烧 普通敌人 {burnPlain} 次 / 澄净 {burnProof} 次；乳化在单位层不结算（普通 {etchPlain} / 澄净 {etchProof}），油斑块 × 酸斑块结算 {etchPatch} 次；爆燃在澄净身上照样 {deflProof} 次——图鉴打法提示写明“这两条要打在其它阵营身上”");
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
            // EMG2-DISC-01（回改 FG10-REACT-01，FGR-EMG-190）：批次开放退役——第一幕也报名字；领地出现在地图上不开放、不通知、不写批次字段。
            CampaignState s = NewWorldState(42);
            PlannedTerritory lake = WorldGenService.PlanFor(s)?.Find(EnemyDefs.FactionClarity);
            Reaction burn = ClarityRows().First(r => r.Id == "reaction_causticburn");
            MechanicCodexEntry entry = MechanicCodex.Find(MechanicCodex.ReactionEntryId(burn.Id));
            bool namedAct1 = lake != null && !HomeExplorationService.TerritoryVisible(s, lake) && ClarityRows().All(r => NamedReactionCatalog.IsNamed(s, r.Id));
            string hint = MechanicCodex.Hint(entry, s);
            bool tipInBody = MechanicCodex.Body(entry, s).Contains(ReactionDiscoveryService.TipOf(burn));
            bool comboHint = hint.StartsWith(GameText.Get("codex.combo.locked_hint").Split('“')[0], StringComparison.Ordinal) && !(StatusTagCatalog.NameOf(burn.TagA) is string tagName && tagName.Length > 0 && hint.Contains(tagName)); // EMG4-CODEX-01：剪影只写通道类别、不给配方
            Expect(namedAct1 && comboHint && tipInBody && (s.OpenReactionBatches?.Length ?? 0) == 0,
                $"D1 第一幕（领地不在地图上）四条都报名字（{namedAct1}），图鉴提示“{hint}”，正文含打法提示（{tipInBody}），批次字段空");

            // D2 种子无关（B25）：种子测试集里每个世界都有澄净领地，第一幕第一次结算就算首次发现。
            var seeds = new List<string>();
            bool allSeeds = true;
            foreach (int seed in new[] { 1, 42, 20261017 })
            {
                CampaignState w = NewWorldState(seed);
                bool hasLake = WorldGenService.PlanFor(w)?.Find(EnemyDefs.FactionClarity) != null;
                int posted0 = ReactionDiscoveryService.Posted;
                bool first = ReactionFeedback.RecordFirst(w, burn.Id, SiteId);
                bool second = ReactionFeedback.RecordFirst(w, burn.Id, SiteId);
                bool ok = hasLake && first && !second && ReactionDiscoveryService.Posted == posted0 + 1;
                allSeeds &= ok;
                seeds.Add($"{seed}:{ok}");
            }
            Expect(allSeeds, $"D2 种子测试集（{string.Join(" ", seeds)}）：都有澄净领地；不等领地显示，第一次结算记录一次、提示一次");
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
                int posted0 = ReactionDiscoveryService.Posted;
                Deflagrate(0);
                bool firstSlow = GameClock.SlowMotionStarts == slow0 + 1 && ReactionFeedback.IsFirstTriggered(s, "reaction_deflagrate");
                NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == ReactionDiscoveryService.NotifyType);
                string text = note?.Latest?.DetailText ?? string.Empty;
                bool told = ReactionDiscoveryService.Posted == posted0 + 1 && text.Contains(GameText.Get("reaction.deflagrate.name"));
                GameClock.CancelSlowMotion();
                int notes1 = ReactionDiscoveryService.Posted;
                Deflagrate(2);
                Deflagrate(4);
                bool noSecond = GameClock.SlowMotionStarts == slow0 + 1 && s.ReactionFirstTriggers.Count(r => r.ReactionId == "reaction_deflagrate") == 1 && ReactionDiscoveryService.Posted == notes1;
                Expect(firstSlow && told && noSecond,
                    $"E1 首次发现（FGR-EMG-190，取代“开放后第一次”）：第一次打出爆燃 → 慢放一次 + 首次记录（{firstSlow}）+ “新发现”通知（“{text}”，{told}）；第二、三次只计数：不慢放、不再提示（{noSecond}）");
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
                int posted0 = ReactionDiscoveryService.Posted;
                GameClock.SetPaused(true);
                for (int i = 0; i < 30; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                bool pausedQuiet = (w.OpenReactionBatches?.Length ?? 0) == 0 && ReactionDiscoveryService.Posted == posted0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                long t0 = GameClock.Ticks;
                for (int i = 0; i < 4000 && GameClock.Ticks < t0 + GameClock.StepHz * 3; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                GameClock.SetSpeed(1f);
                // EMG2-DISC-01：领地在地图上显示也不再有“整秒开放”；世界步进不写批次字段（OpenBatch 已没有生产调用）。
                bool quiet = (w.OpenReactionBatches?.Length ?? 0) == 0 && GameClock.Ticks >= t0 + GameClock.StepHz * 3;
                (bool discovery, bool attribution, int friendly) = DiscoverySpeedScene(w, speed);
                bool friendlyOk = friendly >= 3 && friendly <= 5;
                ok &= pausedQuiet && quiet && discovery && attribution && friendlyOk;
                lines.Add($"{speed}x 暂停无变化 {pausedQuiet} / 走 3 游戏秒不写批次字段 {quiet} / 首次发现一次 {discovery} / 暂停不记交互伤害 {attribution} / 4 游戏秒持续友伤发 {friendly} 条");
            }
            Expect(ok, $"F1 正式世界步进：{string.Join("；", lines)}");

            // F2 真实存档 → 读档（旧档写过的批次字段照样保留，只读）。
            CampaignState s = CampaignSession.Current;
            s.OpenReactionBatches = new[] { Batch };
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
            int postedBack = ReactionDiscoveryService.Posted;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool kept = back.OpenReactionBatches != null && back.OpenReactionBatches.Contains(Batch) && ClarityRows().All(r => NamedReactionCatalog.IsNamed(back, r.Id)
                && NamedReactionCatalog.WasAnnouncedByOldBatch(back, r.Id)) && ReactionDiscoveryService.Posted == postedBack;

            // 旧档没有批次字段：读进来为空，名字照样报，只是没有“已告知过”。
            CampaignState legacy = JsonUtility.FromJson<CampaignState>(SaveMigrationJson.RemoveField(JsonUtility.ToJson(back), "OpenReactionBatches"));
            CampaignFgStateDomains.EnsureAll(legacy);
            bool legacyOk = legacy != null && (legacy.OpenReactionBatches?.Length ?? 0) == 0 && ClarityRows().All(r => NamedReactionCatalog.IsNamed(legacy, r.Id) && !NamedReactionCatalog.WasAnnouncedByOldBatch(legacy, r.Id));
            Expect(s != null && kept && legacyOk,
                $"F2 存读档：读档后批次字段原样、四条都报名字、算已告知，再走 2 个游戏秒不弹提示（{kept}）；旧档无字段 → 为空、照样报名字（{legacyOk}）");

            // F3 旧档走完整读档流程（CampaignRestoreOrchestrator，FG10-REACT-01 遗留 P2）：新字段取缺省，首次记录原样，读档不补弹。
            string firstJson = JsonUtility.ToJson(back).Contains("ReactionFirstTriggers") ? string.Join(",", back.ReactionFirstTriggers.Select(r => r.ReactionId)) : string.Empty;
            SaveResult sv3 = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string strip = FgEmgDiscoverySelfCheck.RewriteAsOldSave(Slot, out int schema3);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            int posted3 = ReactionDiscoveryService.Posted;
            RestoreResult rr3 = CampaignRestoreOrchestrator.Restore(Slot);
            bool old3 = sv3.Success && strip == null && rr3.Success && schema3 == CampaignSaveService.CurrentSchemaVersion && (rr3.State.GrammarDiscoveries?.Length ?? 0) == 0
                        && string.Join(",", rr3.State.ReactionFirstTriggers.Select(r => r.ReactionId)) == firstJson && ReactionDiscoveryService.Posted == posted3;
            if (rr3.Success)
            {
                CampaignSession.Set(Slot, rr3.State);
                HomeValleyController hv3 = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(hv3.SiteId);
                WorldSimulation.StepMany(GameClock.StepHz);
                old3 &= ReactionDiscoveryService.Posted == posted3;
            }
            Expect(old3, $"F3 旧档（本 Story 之前的字段）走完整读档：schema {schema3}、语法发现为空、首次记录原样、读档与之后 1 个游戏秒都不补弹（{strip ?? rr3.Message}）");
        }

        /// <summary>
        /// EMG2-DISC-01 倍速矩阵（B09）：家园一场突袭里 A 的明火点着 B 的油、己方 V 站在 A 的明火里，走真实世界步进。
        /// 返回：爆燃只算一次首次发现；暂停时交互伤害不变、走起来才涨；友伤通知条数（同一组合每游戏秒最多一条）。
        /// </summary>
        private static (bool Discovery, bool Attribution, int Friendly) DiscoverySpeedScene(CampaignState w, float speed)
        {
            CombatSite home = CombatSites.Get(GameLogic.Campaign.Regions.HomeValleyLayout.RegionId);
            CombatKernel k = home.Kernel;
            int Own(double2 at) => k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = at, Home = at, Radius = 0.5f, Health = 100000f, MaxHealth = 100000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
            });
            var c = new double2(120, 120);
            int a = Own(c + new double2(0, 30));
            int b = Own(c + new double2(0, 34));
            int v = Own(c);
            k.Spawn(Hostile(c + new double2(3, 0), -1));
            ReactionAttribution.BeginRaid(w, "disc-" + speed);
            WorldSimulation.StepMany(1);
            k.SpawnPatch(EmgMatterConst.Fire, c, 2f, 1f, 8f, a, false, 7);
            k.SpawnPatch(EmgMatterConst.Oil, c + new double2(3, 0), 2f, 5f, 0f, b, false, 0);
            long friendly0 = ReactionFeedback.FriendlyPosted;
            int posted0 = ReactionDiscoveryService.Posted;
            GameClock.SetSpeed(speed);
            long t0 = GameClock.Ticks;
            for (int i = 0; i < 4000 && GameClock.Ticks < t0 + GameClock.StepHz * 4; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            GameClock.SetSpeed(1f);
            GameClock.CancelSlowMotion();
            int friendly = (int)(ReactionFeedback.FriendlyPosted - friendly0);
            ReactionSessionRecord sess = ReactionAttribution.Current(w, home.SiteId, create: false);
            double dmg = sess?.InteractionDamage ?? 0;
            GameClock.SetPaused(true);
            for (int i = 0; i < 30; i++)
            {
                WorldSimulation.Frame(0.1f);
            }
            bool pausedSame = Math.Abs((sess?.InteractionDamage ?? 0) - dmg) < 1e-9;
            GameClock.SetPaused(false);
            ReactionAttribution.EndRaid(w);
            bool discovery = w.ReactionFirstTriggers.Count(r => r.ReactionId == "reaction_deflagrate") == 1 && ReactionDiscoveryService.Posted > posted0;
            return (discovery, pausedSame && dmg > 0, friendly);
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
