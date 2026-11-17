using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG4-CODEX-01（FG19 FGR-EMG-183 / 194 / 162；FGT-EMG-063、FGT-EMG-062 图鉴部分；DEBT-EMG2RULE01-07 / DEBT-EMG1CHAIN01-04）：组合图鉴。
    /// 起真实地点（CombatSite + 内核 + 通道规则表）跑：每条具名交互一个条目（含 11 条斑块级命名规则）、两侧端口 / 主结果 / 首次快照 / 次数、剪影只给通道类别、语法条目、
    /// 极限（内核按根出手计数 → 热更层记标记 + 技术数据只发一次）、存读档与旧档、跨存档补解锁、不给数值加成、计数接口、单位悬停三段、面板空状态 / ◆ 标记 / 256 条可用 / 布局探针。
    /// 已并入 CellFrameworkValidate.RunAll（段名 FgEmgCodexSelfCheck）。
    /// </summary>
    public static class FgEmgCodexSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const string SiteId = "emg-codex-site";
        private const int Slot = 0;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private static readonly string[] NewNamed =
        {
            "reaction_floatoil", "reaction_freeze", "reaction_dilute", "reaction_quench", "reaction_boiloff", "reaction_smolder", "reaction_dustburst",
            "reaction_scorch", "reaction_steamvent", "reaction_meltpit", "reaction_magsweep",
        };
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("QA/自检/组合图鉴与极限奖励")]
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

        public static int Run(StringBuilder report) =>
            Session(report, "组合图鉴与极限奖励", "FGR-EMG-183 / 194 / 162（FGT-EMG-063 / 062）",
                CheckEntries, CheckDiscoverySnapshot, CheckGrammar, CheckExtreme, CheckSaveLoad, CheckNoBonus, CheckUnitHover, CheckEnemyProgram, CheckPanel);

        /// <summary>EMGR-TALLY-01（FGR-EMG-194、FG00 B10 / D7；DEBT-EMG4CODEX01-01、DEBT-EMGRTALLY01-01 / 02）：极限计数表 4096 格、冲突保新链、进快照段 33、旧档下限。可单独运行。</summary>
        public static int RunTally(StringBuilder report) =>
            Session(report, "极限计数不漏记不误发", "EMGR-TALLY-01：300 条在途链、冲突保新链与丢弃计数、快照段 33 往返、旧档下限（FGR-EMG-194，B10 / D7）",
                CheckTallyFlight, CheckTallyConflict, CheckTallySnapshot, CheckTallyLegacy, CheckTallyNegative);

        private static int Session(StringBuilder report, string tag, string title, params Action[] checks)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line($"\n[{tag}] {title}");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            Func<string> originalObserved = FeedbackCues.ObservedSiteProvider;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgcodex-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                FreshCodex("main");
                FeedbackCues.ObservedSiteProvider = () => null;
                foreach (Action check in checks)
                {
                    Step(check);
                }
            }
            catch (Exception e)
            {
                Fail($"自检抛异常：{e}");
            }
            finally
            {
                GameClock.CancelSlowMotion();
                GameClock.ResetSession();
                FeedbackCues.ObservedSiteProvider = originalObserved;
                NotificationCenter.ResetForTests();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.SetLanguage(originalLanguage);
                CampaignSession.Set(originalSlot, originalSession);
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch (IOException)
                {
                }
            }
            Line($"  · [{tag}] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ───────────────────────────────────────────────────────────────

        private static void FreshCodex(string name)
        {
            MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex-" + name + ".json");
            MechanicCodex.Reload();
        }

        private static CampaignState NewState(int seed)
        {
            GameClock.ResetSession();
            NotificationCenter.ResetForTests();
            ReactionDiscoveryService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgcodex-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            NotificationCenter.Bind(s);
            s.RegionRecords = (s.RegionRecords ?? Array.Empty<RegionRecord>()).Concat(new[] { new RegionRecord { RegionId = SiteId, ExpeditionCount = 1 } }).ToArray();
            return s;
        }

        private static CombatSite NewSite()
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            return new CombatSite(SiteId, cfg);
        }

        private static void StepSite(CombatSite site, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                site.Step(Dt, site.Kernel.Time + Dt);
            }
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, float hp = 100000f) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
            Behavior = CombatBehavior.None, Flags = Live, Position = p, Home = p, Radius = 0.5f, Speed = 0f, Health = hp, MaxHealth = hp,
            Weapon = -1, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f,
            ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
        });

        /// <summary>自检造地点时直接登记“单位 → 机器”（正式流程 AttachMachine 要完整的机器记录与蓝图；快照只用这张映射）。</summary>
        private static void BindMachine(CombatSite site, int unit, int logicId)
        {
            var map = (Dictionary<int, int>)typeof(CombatSite).GetField("_unitMachine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(site);
            map[unit] = logicId;
        }

        private static List<NotificationEntry> Notes(string type) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == type).ToList();

        private static int Key(string id) => NamedReactionCatalog.StableKey(id);

        /// <summary>明火（归属 A）紧贴一块油（归属 B）：热交换把油点着 → 爆燃。</summary>
        private static void Ignite(CombatKernel k, double y, int ownerFire, int ownerOil)
        {
            k.SpawnPatch(EmgMatterConst.Fire, new double2(0, y), 2f, 1f, 8f, ownerFire, false, 7);
            k.SpawnPatch(EmgMatterConst.Oil, new double2(3, y), 2f, 5f, 0f, ownerOil, false, 0);
        }

        private static string Body(string entryId, CampaignState s) => MechanicCodex.Body(MechanicCodex.Find(entryId), s);

        // ── A 条目清单、剪影与通道类别提示 ─────────────────────────────────────

        private static void CheckEntries()
        {
            FreshCodex("entries");
            CampaignState s = NewState(9701);
            var missing = new List<string>();
            foreach (GameConfig.fg.Reaction row in NamedReactionCatalog.Rows)
            {
                MechanicCodexEntry e = MechanicCodex.Find(MechanicCodex.ReactionEntryId(row.Id));
                if (e == null || e.Tab != MechanicCodex.TabReaction || e.Kind != MechanicCodexKind.Reaction)
                {
                    missing.Add(row.Id);
                }
            }
            // 通道规则表里 named 列点名的每个条目都有表行与图鉴条目（冻结桥依赖的水面未落地，DEBT-EMG4TER01-01，承接 FG18-SEA-01）
            var unnamedRules = new List<string>();
            foreach (GameConfig.fg.ChannelRule r in ConfigSystem.Instance.Tables.TbChannelRule.DataList)
            {
                if (r.Named != "none" && r.Named != "reaction_icebridge" && (!NamedReactionCatalog.TryGet(r.Named, out GameConfig.fg.Reaction rr) || rr.Reach != NamedReactionCatalog.Reachable
                    || MechanicCodex.Find(MechanicCodex.ReactionEntryId(r.Named)) == null || NamedReactionCatalog.IdOfKey(Key(r.Named)) != r.Named))
                {
                    unnamedRules.Add(r.Id + "→" + r.Named);
                }
            }
            bool newOnes = NewNamed.All(id => NamedReactionCatalog.TryGet(id, out GameConfig.fg.Reaction row) && row.Kind == NamedReactionCatalog.KindRule
                                               && !GameText.ContainsMarker(NamedReactionCatalog.NameOf(id)) && !GameText.ContainsMarker(NamedReactionCatalog.DescriptionOf(id)));
            Expect(missing.Count == 0 && unnamedRules.Count == 0 && newOnes && NamedReactionCatalog.Rows.Count >= 29,
                $"A1 FGR-EMG-183 / DEBT-EMG2RULE01-07：每条具名交互一个条目（{NamedReactionCatalog.Rows.Count} 条），通道规则 named 列点名的都有表行 + 条目 + 稳定键（含浮油 / 冰封 / 淬火 / 尘爆等 11 条斑块级命名规则）"
                + (missing.Count + unnamedRules.Count > 0 ? "；缺：" + string.Join("、", missing.Concat(unnamedRules)) : string.Empty));

            List<string> grammar = MechanicCodex.GrammarIds().ToList();
            bool grammarEntries = grammar.Count == EmgConst.MaxGen + EmgMetaConst.Kinds + EmgConst.EventKinds
                                  && grammar.All(g => MechanicCodex.Find(MechanicCodex.GrammarEntryId(g)) is MechanicCodexEntry e && e.Tab == MechanicCodex.TabReaction && e.Kind == MechanicCodexKind.Grammar);
            bool grammarText = grammar.All(g =>
            {
                MechanicCodexEntry e = MechanicCodex.Find(MechanicCodex.GrammarEntryId(g));
                string t = MechanicCodex.Title(e), h = MechanicCodex.Hint(e, s);
                return t.Length > 0 && h.Length > 0 && !GameText.ContainsMarker(t) && !GameText.ContainsMarker(h);
            });
            Expect(grammarEntries && grammarText,
                $"A2 语法条目：第 1～{EmgConst.MaxGen} 代子弹药 + {EmgMetaConst.Kinds} 种元程序 + {EmgConst.EventKinds} 种事件 = {grammar.Count} 条，都在组合图鉴页签，标题 / 获取提示走文本键");

            // 未发现：剪影名 + “由哪两类通道产生”，不写端口名（不给配方）
            MechanicCodexEntry floatoil = MechanicCodex.Find(MechanicCodex.ReactionEntryId("reaction_floatoil"));
            string hint = MechanicCodex.Hint(floatoil, s);
            string oil = GameText.Get("emg.port.mat.oil.name"), water = GameText.Get("emg.port.mat.water.name");
            MechanicCodexEntry dust = MechanicCodex.Find(MechanicCodex.ReactionEntryId("reaction_dustburst"));
            string dustHint = MechanicCodex.Hint(dust, s);
            Expect(!MechanicCodex.IsUnlocked(floatoil.Id) && MechanicCodex.ListTitle(floatoil, s) == GameText.Get("codex.panel.locked_title")
                   && hint.Contains(GameText.Get("emg.family.matter")) && !hint.Contains(oil) && !hint.Contains(water)
                   && dustHint.Contains(GameText.Get("emg.family.matter")) && dustHint.Contains(GameText.Get("emg.family.energy")) && !GameText.ContainsMarker(hint + dustHint),
                $"A3 剪影：未发现的浮油只显示“{MechanicCodex.ListTitle(floatoil, s)}”与“{hint}”（通道类别，不出现“{oil}”“{water}”）；尘爆“{dustHint}”");
            Expect(MechanicCodex.EmptyHint(MechanicCodex.TabReaction) == GameText.Get("codex.combo.empty") && MechanicCodex.EmptyHint(MechanicCodex.TabFirmware) == null,
                "A4 负向“第一次打开”：组合图鉴一条都没发现时有空状态提示（其他页签不受影响）");

            // A5（审查 P1）：每条由通道规则产生的具名交互发现后，两侧端口 / 主结果都显示玩家名，不漏出内部端口 / 产物 ID（topo.arc、bhv.stick、ter.mark……），也没有缺键标记
            CampaignState all = NewState(9711);
            var raw = new List<string>();
            var idPattern = new System.Text.RegularExpressions.Regex(@"(mat|en|ter|bhv|topo|evt|coat|phs|ent|hij|chs)[.:][a-z0-9_]");
            int checkedRules = 0;
            foreach (GameConfig.fg.Reaction row in NamedReactionCatalog.Rows)
            {
                if (row == null || row.Kind != NamedReactionCatalog.KindRule)
                {
                    continue;
                }
                ReactionFeedback.RecordFirst(all, row.Id, SiteId);
                string b = Body(MechanicCodex.ReactionEntryId(row.Id), all) ?? string.Empty;
                if (!b.Contains(GameText.Get("codex.combo.result").Split('{')[0]))
                {
                    continue; // 这一行没有通道规则（冻结桥等）：不列端口
                }
                checkedRules++;
                if (idPattern.IsMatch(b) || GameText.ContainsMarker(b))
                {
                    raw.Add(row.Id + "：" + idPattern.Match(b).Value);
                }
            }
            Expect(checkedRules >= 20 && raw.Count == 0,
                $"A5 主结果 / 两侧端口走文本键：{checkedRules} 条有规则行的具名交互正文都不出现内部 ID"
                + (raw.Count > 0 ? "；漏出：" + string.Join("、", raw) : string.Empty)
                + $"（例：导电“{GameText.Get("emg.product.topo.arc.name")}”、熔坑“{GameText.Get("emg.product.ter.mark.name")}”）");
        }

        // ── B 首次发现快照与次数（真实地点）─────────────────────────────────────

        private static void CheckDiscoverySnapshot()
        {
            FreshCodex("snap");
            CampaignState s = NewState(9702);
            int before = ReactionDiscoveryService.DiscoveredInteractionCount();
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int a = Unit(k, CombatFaction.Player, new double2(40, 0));
            int b = Unit(k, CombatFaction.Player, new double2(40, 10));
            BindMachine(site, a, 101);
            BindMachine(site, b, 102);
            Unit(k, CombatFaction.Hostile, new double2(3, 0));
            StepSite(site, 1);
            Ignite(k, 0, a, b);
            int key = Key("reaction_deflagrate");
            for (int i = 0; i < 600 && k.EmgRuleCountOfKey(key) == 0; i++)
            {
                StepSite(site, 1);
            }
            StepSite(site, 2);
            ReactionFirstTriggerRecord r = ReactionFeedback.FirstRecordOf(s, "reaction_deflagrate");
            int kernelCount = k.EmgRuleCountOfKey(key);
            string id = MechanicCodex.ReactionEntryId("reaction_deflagrate");
            string body = Body(id, s);
            string party = r != null && r.Source.Known ? ReactionLog.PartyLabel(r.Source) : "?";
            Expect(r != null && r.Source.Known && (r.Source.LogicId == 101 || r.Source.LogicId == 102) && r.HasPos && Math.Abs(r.X - 3f) < 3.5f && r.SiteId == SiteId
                   && r.Count == kernelCount && kernelCount > 0 && MechanicCodex.IsUnlocked(id)
                   && body.Contains(GameText.Format("codex.combo.count", kernelCount)) && body.Contains(party)
                   && body.Contains(GameText.Get("codex.combo.ports").Split('{')[0]) && body.Contains(GameText.Get("codex.combo.result").Split('{')[0])
                   && body.Contains(GameText.Get("emg.port.mat.oil.name")) && !GameText.ContainsMarker(body),
                $"B1 FGT-EMG-063：第一次结算爆燃 → 条目写两侧端口、主结果、首次快照（{party}，({r?.X:0.#}, {r?.Y:0.#})，{CombatSites.SiteName(r?.SiteId)}）、已触发 {r?.Count} 次（= 内核可靠计数 {kernelCount}）：\n        {body.Replace("\n", " | ")}");
            float x0 = r?.X ?? 0f;
            Ignite(k, 30, a, b);
            int k1 = kernelCount;
            for (int i = 0; i < 600 && k.EmgRuleCountOfKey(key) == k1; i++)
            {
                StepSite(site, 1);
            }
            StepSite(site, 2);
            int k2 = k.EmgRuleCountOfKey(key);
            Expect(r != null && k2 > k1 && r.Count == k2 && Math.Abs(r.X - x0) < 1e-4f && s.ReactionFirstTriggers.Count(x => x.ReactionId == "reaction_deflagrate") == 1,
                $"B2 负向：第二次只累加次数（{k1} → {r?.Count}，内核 {k2}），首次快照不被覆盖（x {x0:0.#}）");

            // 斑块级命名规则（DEBT-EMG2RULE01-07）：水 + 液氮整片结冰 → 冰封条目第一次发现
            k.SpawnPatch(EmgMatterConst.Water, new double2(-30, 0), 2f, 3f, 0f);
            k.SpawnPatch(EmgMatterConst.Cryo, new double2(-30, 0.5), 2f, 3f, 0f);
            int fk = Key("reaction_freeze");
            for (int i = 0; i < 600 && k.EmgRuleCountOfKey(fk) == 0; i++)
            {
                StepSite(site, 1);
            }
            StepSite(site, 2);
            ReactionFirstTriggerRecord fr = ReactionFeedback.FirstRecordOf(s, "reaction_freeze");
            string fid = MechanicCodex.ReactionEntryId("reaction_freeze");
            bool note = Notes(ReactionDiscoveryService.NotifyType).Any(n => n.Members.Any(m => m.DetailText.Contains(NamedReactionCatalog.NameOf("reaction_freeze"))));
            Expect(k.EmgRuleCountOfKey(fk) > 0 && fr != null && fr.Count == k.EmgRuleCountOfKey(fk) && MechanicCodex.IsUnlocked(fid) && note
                   && Body(fid, s).Contains(GameText.Get("emg.port.mat.cryo.name")),
                $"B3 DEBT-EMG2RULE01-07：斑块级规则“冰封”第一次结算 → 首次发现通知、组合图鉴条目解锁、次数 {fr?.Count}（内核 {k.EmgRuleCountOfKey(fk)}）");
            int after = ReactionDiscoveryService.DiscoveredInteractionCount();
            Expect(after - before == 2 && ReactionDiscoveryService.DiscoveredInteractionCount(withGrammar: true) >= after,
                $"B4 计数接口（FG15-SYS-04 成就 / FG13-END-06 结局统计）：组合图鉴发现 {before} → {after} 种交互");
        }

        // ── C 语法条目 ─────────────────────────────────────────────────────────

        private static void CheckGrammar()
        {
            FreshCodex("grammar");
            CampaignState s = NewState(9703);
            string gid = MechanicCodex.GrammarEntryId(ReactionDiscoveryService.GenEntry(1));
            bool locked = !MechanicCodex.IsUnlocked(gid) && MechanicCodex.UnlockedCountOf(MechanicCodexKind.Grammar) == 0;
            bool first = ReactionDiscoveryService.RecordGrammar(s, ReactionDiscoveryService.GenEntry(1), SiteId);
            bool meta = ReactionDiscoveryService.RecordGrammar(s, ReactionDiscoveryService.MetaEntry(0), SiteId);
            string body = Body(gid, s);
            Expect(locked && first && meta && MechanicCodex.IsUnlocked(gid) && MechanicCodex.IsUnlocked(MechanicCodex.GrammarEntryId(ReactionDiscoveryService.MetaEntry(0)))
                   && MechanicCodex.UnlockedCountOf(MechanicCodexKind.Grammar) == 2 && body.Contains(GameText.Get("codex.grammar.first").Split('{')[0])
                   && ReactionDiscoveryService.DiscoveredInteractionCount(withGrammar: true) - ReactionDiscoveryService.DiscoveredInteractionCount() == 2,
                $"C1 FGR-EMG-183 语法条目：第一次组成第 1 代子弹药 / 用到元程序 → 组合图鉴条目解锁，正文写本存档首次（“{body.Replace("\n", " | ")}”）");
            CampaignState other = NewState(9704);
            Expect(MechanicCodex.IsUnlocked(gid) && Body(gid, other).Contains(GameText.Get("codex.grammar.elsewhere")),
                "C2 D-17 跨存档共享：另一份存档里条目仍解锁，正文写“在别的存档里发现过”");
        }

        // ── D 极限（内核按根出手计数 → 热更层标记 + 奖励只发一次）───────────────

        private static void CheckExtreme()
        {
            FreshCodex("extreme");
            CampaignState s = NewState(9705);
            foreach (string id in new[] { "reaction_deflagrate", "reaction_conduct", "reaction_dustburst" })
            {
                ReactionFeedback.RecordFirst(s, id, SiteId);
            }
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int me = Unit(k, CombatFaction.Player, new double2(0, 0));
            StepSite(site, 1);
            int need = ReactionDiscoveryService.ExtremeCount;
            int reward = ReactionDiscoveryService.ExtremeReward;
            long tech0 = s.TechData;

            void Rules(int chain, string id, int n, byte depth = 0)
            {
                for (int i = 0; i < n; i++)
                {
                    k.EmgChainLogForTests(EmgChainNode.Rule, chain, me, 0, 0, Key(id), depth);
                }
            }

            int c1 = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Shot, c1, me);
            Rules(c1, "reaction_deflagrate", need - 1);
            StepSite(site, 1);
            ReactionFirstTriggerRecord r = ReactionFeedback.FirstRecordOf(s, "reaction_deflagrate");
            bool notYet = !r.Extreme && s.TechData == tech0;
            Rules(c1, "reaction_deflagrate", 1);
            bool paused = !r.Extreme; // 内核没步进（暂停）：还没发布，不记
            StepSite(site, 3);
            bool marked = r.Extreme && r.ExtremeSiteId == SiteId && s.TechData == tech0 + reward && CampaignEconomyLedger.Find(s, ReactionDiscoveryService.ExtremeTxPrefix + "reaction_deflagrate") != null
                          && TechDataFlow.IncomeOf(s, TechDataFlow.Codex) == reward && Notes(ReactionDiscoveryService.ExtremeNotifyType).Count == 1;
            Expect(need == 12 && reward == 20 && notYet && paused && marked,
                $"D1 FGR-EMG-194：一次根出手结算 {need - 1} 次不记；第 {need} 次（暂停时不记，步进后）记“极限”，技术数据 {tech0} → {s.TechData}（+{reward}，统计面板“组合图鉴极限”{TechDataFlow.IncomeOf(s, TechDataFlow.Codex)}）、信息级通知");

            int c2 = k.EmgIssueChainForTests();
            Rules(c2, "reaction_deflagrate", need + 5);
            StepSite(site, 2);
            Expect(s.TechData == tech0 + reward && ReactionDiscoveryService.ExtremeRewarded == 1 && Notes(ReactionDiscoveryService.ExtremeNotifyType).Sum(n => n.Count) == 1,
                $"D2 负向：同一条目第二次达到极限不再发奖励、不再通知（累计发放 {ReactionDiscoveryService.ExtremeRewarded} 次）");

            int c3 = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Shot, c3, me);
            Rules(c3, "reaction_conduct", 1, depth: 2);
            StepSite(site, 1);
            bool depth2 = !ReactionFeedback.FirstRecordOf(s, "reaction_conduct").Extreme;
            k.EmgChainLogForTests(EmgChainNode.Trigger, c3, me, 0, 0, 0, 3);
            StepSite(site, 1);
            Expect(depth2 && ReactionFeedback.FirstRecordOf(s, "reaction_conduct").Extreme && s.TechData == tech0 + 2 * reward,
                "D3 链深：一次根出手链深 2 不记；链深达到 3 → 记在这条链最近一条规则（短路）上，再发一次（别的条目）");

            int c4 = k.EmgIssueChainForTests();
            Rules(c4, "reaction_quench", need + 1);
            StepSite(site, 1);
            Expect(ReactionFeedback.FirstRecordOf(s, "reaction_quench") == null && s.TechData == tech0 + 2 * reward,
                "D4 负向：本存档还没发现的条目达到极限不记、不发（理论上同一步先发现，后记极限）");

            // 倍速：同样的根出手，1 步 / 3 步内处理完结果一样（只按内核步，不按帧）；多根出手在同一步也逐条处理
            int c5 = k.EmgIssueChainForTests(), c6 = k.EmgIssueChainForTests();
            Rules(c5, "reaction_dustburst", need);
            Rules(c6, "reaction_dustburst", need);
            StepSite(site, 1);
            StepSite(site, 2);
            Expect(ReactionFeedback.FirstRecordOf(s, "reaction_dustburst").Extreme && s.TechData == tech0 + 3 * reward && ReactionDiscoveryService.ExtremeRewarded == 3,
                $"D5 两条根出手同一步都满 {need} 次：只记一次、只发一次（累计 {ReactionDiscoveryService.ExtremeRewarded} 次）");
            string body = Body(MechanicCodex.ReactionEntryId("reaction_deflagrate"), s);
            MechanicCodexEntry e = MechanicCodex.Find(MechanicCodex.ReactionEntryId("reaction_deflagrate"));
            Expect(body.Contains("◆") && MechanicCodex.ListTitle(e, s).StartsWith("◆ ", StringComparison.Ordinal) && MechanicCodex.IsExtreme(e, s)
                   && !Body(MechanicCodex.ReactionEntryId("reaction_steam"), s).Contains("◆"),
                $"D6 B15 形状为主：记过极限的条目列表名前加 ◆、正文写极限时刻 / 地点（“{MechanicCodex.ListTitle(e, s)}”）");

            // D7 负向（审查 P2）：极限是玩家破格组合的奖励——敌方根出手满门槛、链深 3 都不记、不发
            int foe = Unit(k, CombatFaction.Hostile, new double2(30, 0));
            StepSite(site, 1);
            ReactionFeedback.RecordFirst(s, "reaction_sticky", SiteId);
            int c7 = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Shot, c7, foe);
            for (int i = 0; i < need + 2; i++)
            {
                k.EmgChainLogForTests(EmgChainNode.Rule, c7, foe, 0, 0, Key("reaction_sticky"), 3);
            }
            StepSite(site, 2);
            Expect(!ReactionFeedback.FirstRecordOf(s, "reaction_sticky").Extreme && s.TechData == tech0 + 3 * reward && ReactionDiscoveryService.ExtremeRewarded == 3,
                $"D7 负向：敌方单位根出手结算 {need + 2} 次、链深 3 → 不记极限、不发技术数据（{s.TechData - tech0}）");

            // D8（审查 P2）：链在无名规则（沸腾、融化……没有图鉴条目）上越过门槛 → 极限记在这条链最近一条有条目的规则上；全是无名规则的链不记
            ReactionFeedback.RecordFirst(s, "reaction_freeze", SiteId);
            int c8 = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Shot, c8, me);
            Rules(c8, "reaction_freeze", 1);
            for (int i = 0; i < need; i++)
            {
                k.EmgChainLogForTests(EmgChainNode.Rule, c8, me, 0, 0, Key("M_unnamed_selfcheck"), 0, codex: false);
            }
            int c9 = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Shot, c9, me);
            for (int i = 0; i < need + 2; i++)
            {
                k.EmgChainLogForTests(EmgChainNode.Rule, c9, me, 0, 0, Key("M_unnamed_selfcheck"), 3, codex: false);
            }
            int marked0 = ReactionDiscoveryService.ExtremeMarked;
            StepSite(site, 2);
            Expect(ReactionFeedback.FirstRecordOf(s, "reaction_freeze").Extreme && s.TechData == tech0 + 4 * reward && ReactionDiscoveryService.ExtremeMarked == marked0 + 1,
                $"D8 无名规则：越过门槛的那次落在无名规则上，极限仍记到这条链上一条具名交互“冰封”（+{reward}）；只有无名规则的链不记（本步新记 {ReactionDiscoveryService.ExtremeMarked - marked0} 条）");
        }

        // ── T 极限计数表（EMGR-TALLY-01：4096 格、冲突保新链、快照段 33、旧档下限）─────────

        private static int NeedOf(CombatKernel k) => k.Config.EmgExtremeCount > 0 ? k.Config.EmgExtremeCount : EmgConst.DefaultExtremeCount;

        private static void TallyRules(CombatKernel k, int chain, int src, int key, int n, byte depth = 0)
        {
            for (int i = 0; i < n; i++)
            {
                k.EmgChainLogForTests(EmgChainNode.Rule, chain, src, 0, 0, key, depth);
            }
        }

        private static List<int> Extremes(CombatKernel k) => Enumerable.Range(0, k.EmgExtremeRows).Select(k.EmgExtremeAt).ToList();

        private static void KStep(CombatKernel k) => k.Step(Dt, k.Time + Dt);

        /// <summary>只有内核的夹具（存读档逐位比较用）：一台己方、一台敌方，都不开火。</summary>
        private static CombatKernel TallyKernel(out int me, out int foe)
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            var k = new CombatKernel(cfg, 64);
            me = Unit(k, CombatFaction.Player, new double2(0, 0));
            foe = Unit(k, CombatFaction.Hostile, new double2(30, 0));
            KStep(k);
            return k;
        }

        private static int SlotOfChain(int chain) => chain & (EmgConst.ChainTally - 1);

        private static void CheckTallyFlight()
        {
            FreshCodex("tally-flight");
            CampaignState s = NewState(9711);
            ReactionFeedback.RecordFirst(s, "reaction_deflagrate", SiteId);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int me = Unit(k, CombatFaction.Player, new double2(0, 0));
            StepSite(site, 1);
            int need = NeedOf(k), key = Key("reaction_deflagrate"), reward = ReactionDiscoveryService.ExtremeReward;
            long tech0 = s.TechData;
            var chains = new int[300];
            for (int i = 0; i < chains.Length; i++)
            {
                chains[i] = k.EmgIssueChainForTests();
                k.EmgChainLogForTests(EmgChainNode.Shot, chains[i], me);
                TallyRules(k, chains[i], me, key, need - 1);
            }
            StepSite(site, 1);
            int before = k.EmgExtremeRows;
            TallyRules(k, chains[0], me, key, 1); // 最早开的链在 299 条更新的链之后才越过门槛
            StepSite(site, 1);
            List<int> first = Extremes(k);
            bool rewarded = ReactionFeedback.FirstRecordOf(s, "reaction_deflagrate").Extreme && s.TechData == tech0 + reward;
            for (int i = 1; i < chains.Length; i++)
            {
                TallyRules(k, chains[i], me, key, 1);
            }
            StepSite(site, 1);
            int rest = k.EmgExtremeRows;
            Expect(EmgConst.ChainTally == 4096 && before == 0 && first.Count == 1 && first[0] == key && rewarded && rest == chains.Length - 1 && s.TechData == tech0 + reward
                   && k.PatchStats.TallyEvicted == 0 && SlotOfChain(chains[256]) != SlotOfChain(chains[0]),
                $"T1 300 条根出手同时在途（各差 1 次到门槛 {need}）：最早那条在 299 条新链之后越过门槛仍记极限（{first.Count} 条，技术数据 +{s.TechData - tech0}），"
                + $"其余 299 条同一步各记一次（{rest} 条，同一条目只发一次奖励），冲突丢弃 {k.PatchStats.TallyEvicted}（旧 256 格时第 1 与第 257 条同格会互相清零）");
        }

        private static void CheckTallyConflict()
        {
            FreshCodex("tally-conflict");
            CampaignState s = NewState(9712);
            ReactionFeedback.RecordFirst(s, "reaction_sticky", SiteId);
            ReactionFeedback.RecordFirst(s, "reaction_conduct", SiteId);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int me = Unit(k, CombatFaction.Player, new double2(0, 0));
            int foe = Unit(k, CombatFaction.Hostile, new double2(30, 0));
            StepSite(site, 1);
            int need = NeedOf(k), sticky = Key("reaction_sticky"), conduct = Key("reaction_conduct");
            long tech0 = s.TechData;
            int enemy = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Shot, enemy, foe); // 敌方根出手：这条链封印
            int mine = 0;
            for (int i = 0; i < EmgConst.ChainTally; i++)
            {
                mine = k.EmgIssueChainForTests(); // 第 4096 条新链与敌方链同格
            }
            k.EmgChainLogForTests(EmgChainNode.Shot, mine, me);
            TallyRules(k, mine, me, conduct, need - 1);
            int ev0 = k.PatchStats.TallyEvicted;
            TallyRules(k, enemy, foe, sticky, need + 2, 3); // 敌方旧链在新链之后回来：链深 3、超过门槛
            int dropped = k.PatchStats.TallyEvicted - ev0;
            StepSite(site, 1);
            int enemyRows = k.EmgExtremeRows;
            bool enemyQuiet = !ReactionFeedback.FirstRecordOf(s, "reaction_sticky").Extreme && s.TechData == tech0;
            TallyRules(k, mine, me, conduct, 1);
            StepSite(site, 1);
            List<int> mineRows = Extremes(k);
            bool mineOk = mineRows.Count == 1 && mineRows[0] == conduct && ReactionFeedback.FirstRecordOf(s, "reaction_conduct").Extreme && s.TechData == tech0 + ReactionDiscoveryService.ExtremeReward;
            Expect(SlotOfChain(mine) == SlotOfChain(enemy) && enemyRows == 0 && enemyQuiet && mineOk,
                $"T2 D7 敌方链被新链挤格（链 {enemy} 与 {mine} 同格）：敌方旧链在新链之后回来结算 {need + 2} 次、链深 3 → 不记、不发技术数据；"
                + $"新链的计数没被旧链清掉，再 1 次即到门槛记极限（+{s.TechData - tech0}）");
            string line = site.EmgPatchDebugLine();
            Expect(dropped == need + 2 && line.Contains("极限计数冲突丢弃 " + k.PatchStats.TallyEvicted),
                $"T3 冲突丢弃计数可见：旧链这 {need + 2} 次结算没计、TallyEvicted +{dropped}，调试层一行写着（…{line.Substring(Math.Max(0, line.Length - 24))}）");
        }

        private static void CheckTallySnapshot()
        {
            using CombatKernel a = TallyKernel(out int me, out int foe);
            int need = NeedOf(a), k1 = Key("reaction_deflagrate"), k2 = Key("reaction_conduct");
            int p = a.EmgIssueChainForTests(), e = a.EmgIssueChainForTests(), x = a.EmgIssueChainForTests();
            ulong h0 = a.StateHash(), eh0 = a.EmgStateHash();
            byte[] empty = a.Serialize(), emptyPre = a.SerializePreTallyForTests();
            a.EmgChainLogForTests(EmgChainNode.Shot, p, me);
            a.EmgChainLogForTests(EmgChainNode.Shot, e, foe);
            a.EmgChainLogForTests(EmgChainNode.Shot, x, me);
            TallyRules(a, p, me, k1, need - 1, 2); // 在途：差 1 次
            TallyRules(a, e, foe, k2, 3); // 敌方：已封印
            TallyRules(a, x, me, k2, need); // 已记过极限
            bool hashSame = a.StateHash() == h0 && a.EmgStateHash() == eh0;
            KStep(a);
            int[] markedAtSave = Extremes(a).ToArray();
            int4[] atSave = a.EmgChainTallyForTests();
            int rows = atSave.Count(t => t.x != 0);
            byte[] snap = a.Serialize(), pre = a.SerializePreTallyForTests();
            using var b = new CombatKernel(a.Config, 64);
            CombatLoadResult lr = b.Load(snap);
            bool same = lr == CombatLoadResult.Ok && b.EmgChainTallyForTests().SequenceEqual(atSave) && b.PatchStats.TallyFloor == 0 && b.StateHash() == a.StateHash() && b.EmgStateHash() == a.EmgStateHash();
            Expect(hashSame && markedAtSave.Length == 1 && same && rows == 3 && empty.Length - emptyPre.Length == 4 + 1 && snap.Length - pre.Length == 4 + 1 + 14 + 16 * rows, // EMGR-E2E-01：标量段第 29 字段 MatterFlushPending 1 B 一并只在新形里写
                $"T4a 快照段 33 往返：3 行（在途 / 敌方封印 / 已记极限）读回逐位一致（{lr}），下限 0；计数不进状态哈希（结算前后哈希不变）；"
                + $"空表不写段 33、只多标量第 28 字段 4 B（+ 第 29 字段 1 B），有计数时段 33 = 14 B 段头 + {rows} × 16 B（快照 {snap.Length} / 旧形 {pre.Length} 字节）");

            void Follow(CombatKernel k)
            {
                TallyRules(k, p, me, k1, 1, 2);
                TallyRules(k, e, foe, k2, need + 2, 3);
                TallyRules(k, x, me, k2, 2);
                KStep(k);
            }

            Follow(a);
            Follow(b);
            List<int> ra = Extremes(a), rb = Extremes(b);
            Expect(ra.Count == 1 && ra[0] == k1 && ra.SequenceEqual(rb) && a.EmgChainTallyForTests().SequenceEqual(b.EmgChainTallyForTests()) && a.StateHash() == b.StateHash(),
                $"T4b B10 存档后接着玩 = 一直玩（暂停中存读档、没走步）：在途链再 1 次即记极限（不存档 {ra.Count} 条 / 读档后 {rb.Count} 条，同一条目），敌方封印链结算 {need + 2} 次仍不记，"
                + "已记过的不重记；之后两边计数表逐位一致");
        }

        private static void CheckTallyLegacy()
        {
            using CombatKernel a = TallyKernel(out int me, out int foe);
            int need = NeedOf(a), k1 = Key("reaction_deflagrate"), k2 = Key("reaction_sticky");
            int p = a.EmgIssueChainForTests(), e = a.EmgIssueChainForTests();
            a.EmgChainLogForTests(EmgChainNode.Shot, p, me);
            a.EmgChainLogForTests(EmgChainNode.Shot, e, foe);
            TallyRules(a, p, me, k1, need - 1);
            KStep(a);
            int4[] atSave = a.EmgChainTallyForTests();
            byte[] old = a.SerializePreTallyForTests(), fresh = a.Serialize(), v19 = a.SerializeFormatForTests(19);
            using var c = new CombatKernel(a.Config, 64);
            CombatLoadResult lo = c.Load(old);
            int floor = c.PatchStats.TallyFloor, next = c.EmgScalars.NextChain;
            bool emptyTable = c.EmgChainTallyForTests().All(t => t.x == 0);
            TallyRules(c, e, foe, k2, need + 2, 3); // 敌方链的封印位随旧档丢了：没有下限就会被当成新链误发
            TallyRules(c, p, me, k1, 1);
            KStep(c);
            int legacyRows = c.EmgExtremeRows;
            int n = c.EmgIssueChainForTests();
            c.EmgChainLogForTests(EmgChainNode.Shot, n, me);
            TallyRules(c, n, me, k1, need);
            KStep(c);
            List<int> newRows = Extremes(c);
            Expect(lo == CombatLoadResult.Ok && old.Length < fresh.Length && emptyTable && floor == next && floor > e && legacyRows == 0 && newRows.Count == 1 && newRows[0] == k1,
                $"T5 D7 读本卡之前的旧档（标量段 27 字段、没有段 33）：可读（{lo}），下限 = 读档时的下一个链号 {floor}；在途敌方链结算 {need + 2} 次、链深 3 不记不发（{legacyRows} 条），"
                + $"在途己方链这次少记（DEBT-EMGRTALLY01-02 宁少记不误发）；读档后新开的链照常记极限（{newRows.Count} 条）");
            using var c19 = new CombatKernel(a.Config, 64);
            CombatLoadResult l19 = c19.Load(v19);
            Expect(l19 == CombatLoadResult.Ok && c19.PatchStats.TallyFloor == c19.EmgScalars.NextChain && c19.PatchStats.TallyFloor > e,
                $"T5b 格式 19 旧档（{l19}）同样取下限 = 下一个链号 {c19.PatchStats.TallyFloor}");

            // 负向：旧档 + 新档交替读——下限与计数表每次都按这份档重来，不带上一份
            using var d = new CombatKernel(a.Config, 64);
            CombatLoadResult l1 = d.Load(old);
            int f1 = d.PatchStats.TallyFloor;
            CombatLoadResult l2 = d.Load(fresh);
            int f2 = d.PatchStats.TallyFloor;
            bool tbl2 = d.EmgChainTallyForTests().SequenceEqual(atSave);
            TallyRules(d, p, me, k1, 1);
            KStep(d);
            int r2 = d.EmgExtremeRows;
            CombatLoadResult l3 = d.Load(old);
            int f3 = d.PatchStats.TallyFloor;
            bool tbl3 = d.EmgChainTallyForTests().All(t => t.x == 0);
            TallyRules(d, e, foe, k2, need + 2, 3);
            TallyRules(d, p, me, k1, 1);
            KStep(d);
            int r3 = d.EmgExtremeRows;
            Expect(l1 == CombatLoadResult.Ok && l2 == CombatLoadResult.Ok && l3 == CombatLoadResult.Ok && f1 == next && f2 == 0 && tbl2 && r2 == 1 && f3 == next && tbl3 && r3 == 0,
                $"T6 负向 旧档 → 新档 → 旧档交替读：下限 {f1} → {f2} → {f3}；读新档后计数表逐位还原、在途链再 1 次即记（{r2} 条）；再读旧档后表清空、在途链不记不发（{r3} 条）");
        }

        private static void CheckTallyNegative()
        {
            using CombatKernel k = TallyKernel(out int me, out _);
            int need = NeedOf(k), key = Key("reaction_deflagrate"), last = EmgConst.ChainTally;
            // 计数表满：4096 条链各占一格（各结算 1 次），第 4097 条与第 1 条同格
            var chains = new int[EmgConst.ChainTally + 1];
            for (int i = 0; i < last; i++)
            {
                chains[i] = k.EmgIssueChainForTests();
                k.EmgChainLogForTests(EmgChainNode.Shot, chains[i], me);
                TallyRules(k, chains[i], me, key, 1);
            }
            int4[] full = k.EmgChainTallyForTests();
            int fullRows = full.Count(t => t.x != 0);
            byte[] fullSnap = k.Serialize(), preSnap = k.SerializePreTallyForTests();
            int segBytes = fullSnap.Length - preSnap.Length - 4 - 1; // 标量第 28 字段 4 B + 第 29 字段 MatterFlushPending 1 B（EMGR-E2E-01）
            using var kr = new CombatKernel(k.Config, 64);
            bool fullRound = kr.Load(fullSnap) == CombatLoadResult.Ok && kr.EmgChainTallyForTests().SequenceEqual(full);
            chains[last] = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Shot, chains[last], me);
            TallyRules(k, chains[last], me, key, need - 1);
            int ev0 = k.PatchStats.TallyEvicted;
            TallyRules(k, chains[0], me, key, need); // 最早那条：格已被第 4097 条占了
            int dropped = k.PatchStats.TallyEvicted - ev0;
            KStep(k);
            int rowsOld = k.EmgExtremeRows;
            TallyRules(k, chains[last], me, key, 1);
            KStep(k);
            int rowsNew = k.EmgExtremeRows;
            Expect(fullRows == EmgConst.ChainTally && fullRound && SlotOfChain(chains[last]) == SlotOfChain(chains[0]) && dropped == need && rowsOld == 0 && rowsNew == 1
                   && segBytes == 14 + 16 * EmgConst.ChainTally,
                $"T7 负向 计数表满（{fullRows} 行，每内核 {full.Length * 16 / 1024} KB，原 256 格 4 KB）：整表存读档逐位一致，快照段 33 = {segBytes} B（14 B 段头 + 4096 × 16 B）；"
                + $"第 4097 条链挤掉第 1 条，第 1 条再结算 {need} 次没计（丢弃 +{dropped}，误发 {rowsOld} 条），新链照常记极限（{rowsNew} 条）");

            // 链号回绕：c、c + 4096、c + 8192 落同一格——更大的链号占格，更小的来者丢弃计数、不清掉大链号的计数
            using CombatKernel w = TallyKernel(out int wm, out _);
            int c0 = w.EmgIssueChainForTests(), c1 = 0, c2 = 0;
            for (int i = 0; i < 2 * EmgConst.ChainTally; i++)
            {
                int c = w.EmgIssueChainForTests();
                c1 = c == c0 + EmgConst.ChainTally ? c : c1;
                c2 = c == c0 + 2 * EmgConst.ChainTally ? c : c2;
            }
            TallyRules(w, c0, wm, key, 3);
            TallyRules(w, c2, wm, key, need - 1);
            int e0 = w.PatchStats.TallyEvicted;
            TallyRules(w, c1, wm, key, need);
            TallyRules(w, c0, wm, key, need);
            int dr = w.PatchStats.TallyEvicted - e0;
            int4 slot = w.EmgChainTallyForTests()[SlotOfChain(c0)];
            KStep(w);
            int r8a = w.EmgExtremeRows;
            TallyRules(w, c2, wm, key, 1);
            KStep(w);
            int r8b = w.EmgExtremeRows;
            Expect(c1 > 0 && c2 > 0 && SlotOfChain(c1) == SlotOfChain(c0) && SlotOfChain(c2) == SlotOfChain(c0) && dr == 2 * need && slot.x == c2 && slot.y == need - 1 && r8a == 0 && r8b == 1,
                $"T8 负向 链号回绕（{c0} / {c1} / {c2} 同格）：{c2} 占格后 {c1}、{c0} 各结算 {need} 次都丢弃（+{dr}）、不清 {c2} 的计数（{slot.y} 次），{c2} 再 1 次即记（{r8b} 条），误发 {r8a} 条");

            // 同一步同一条链多次结算：只记一次、封印后不再累计
            int s9 = w.EmgIssueChainForTests();
            w.EmgChainLogForTests(EmgChainNode.Shot, s9, wm);
            TallyRules(w, s9, wm, key, 3 * need, 3);
            KStep(w);
            int r9 = w.EmgExtremeRows;
            int4 t9 = w.EmgChainTallyForTests()[SlotOfChain(s9)];
            TallyRules(w, s9, wm, key, need);
            KStep(w);
            Expect(r9 == 1 && (t9.z & EmgConst.TallyExtremeBit) != 0 && t9.y == 1 && w.EmgExtremeRows == 0,
                $"T9 负向 同一步同一条链结算 {3 * need} 次（链深 3）：本步只记 {r9} 次极限、第一次就封印（计到 {t9.y} 次即停）；下一步再结算 {need} 次不重记（{w.EmgExtremeRows} 条）");
        }

        // ── E 存读档、旧档、跨存档补解锁 ─────────────────────────────────────

        [Serializable]
        private sealed class Wrap
        {
            public ReactionFirstTriggerRecord[] Items;
        }

        private static void CheckSaveLoad()
        {
            FreshCodex("save");
            CampaignState s = NewState(9706);
            ReactionFeedback.RecordFirst(s, "reaction_deflagrate", SiteId);
            ReactionFirstTriggerRecord r = ReactionFeedback.FirstRecordOf(s, "reaction_deflagrate");
            r.Source = new ReactionLogParty { Known = true, Kind = CombatUnitKind.Machine, LogicId = 101 };
            r.FirmwareIds = new[] { "fw_oilslick" };
            r.PartIds = new[] { "comp_flamer" };
            r.HasPos = true;
            r.X = 12.5f;
            r.Y = -3f;
            r.Count = 37;
            ReactionDiscoveryService.OnExtreme(s, "reaction_deflagrate", SiteId);
            ReactionDiscoveryService.RecordGrammar(s, ReactionDiscoveryService.EventEntry(1), SiteId);
            long tech = s.TechData;
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(Slot);
            ReactionFirstTriggerRecord b = ReactionFeedback.FirstRecordOf(loaded.State, "reaction_deflagrate");
            bool fields = b != null && b.Source.Known && b.Source.LogicId == 101 && b.Source.Kind == CombatUnitKind.Machine && b.FirmwareIds.SequenceEqual(r.FirmwareIds)
                          && b.PartIds.SequenceEqual(r.PartIds) && b.HasPos && b.X == 12.5f && b.Y == -3f && b.Count == 37 && b.Extreme && b.ExtremeTick == r.ExtremeTick;
            int rewarded = ReactionDiscoveryService.ExtremeRewarded;
            bool again = loaded.State != null && !ReactionDiscoveryService.OnExtreme(loaded.State, "reaction_deflagrate", SiteId) && loaded.State.TechData == tech
                         && ReactionDiscoveryService.ExtremeRewarded == rewarded;
            Expect(saved.Success && loaded.Success && fields && again,
                "E1 真实存档 → 读档：首次快照（出手方 / 固件 / 部件 / 坐标）、次数、极限标记原样；读档后同一条不再记、不再发奖励");

            // 跨存档 / 换机：图鉴文件丢了（新机器），读档时按存档补解锁具名交互与语法条目（旧档已发现条目完整）
            FreshCodex("save-newmachine");
            bool lockedNow = !MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId("reaction_deflagrate"));
            MechanicCodex.SyncContent(loaded.State);
            Expect(lockedNow && MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId("reaction_deflagrate")) && MechanicCodex.IsUnlocked(MechanicCodex.GrammarEntryId(ReactionDiscoveryService.EventEntry(1))),
                "E2 读档时已发现条目完整：图鉴文件不在时 SyncContent 按存档补回具名交互与语法条目");

            // 旧档：本卡之前的首次记录（只有 ReactionId / Tick / SiteId）读入后字段取缺省，条目照常显示（次数至少 1、没有极限）
            string oldJson = "{\"Items\":[{\"ReactionId\":\"reaction_conduct\",\"Tick\":5,\"SiteId\":\"" + SiteId + "\"}]}";
            Wrap w = JsonUtility.FromJson<Wrap>(oldJson);
            ReactionFirstTriggerRecord o = w?.Items?.FirstOrDefault();
            CampaignState os = NewState(9707);
            os.ReactionFirstTriggers = w?.Items ?? Array.Empty<ReactionFirstTriggerRecord>();
            MechanicCodex.SyncContent(os);
            string body = Body(MechanicCodex.ReactionEntryId("reaction_conduct"), os);
            Expect(o != null && !o.Extreme && o.Count == 0 && !o.HasPos && !o.Source.Known && MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId("reaction_conduct"))
                   && body.Contains(GameText.Format("codex.combo.count", 1)) && !body.Contains("◆") && !GameText.ContainsMarker(body),
                $"E3 旧档首次记录（无快照字段）读入：极限否、次数显示 1、条目解锁且正文正常（“{body.Replace("\n", " | ")}”）");
        }

        // ── F 只记录发现、不给数值加成 ───────────────────────────────────────

        private static void CheckNoBonus()
        {
            FreshCodex("nobonus");
            NewState(9708);
            string before = JsonUtility.ToJson(new ConfigBox { C = CombatSite.ConfigFromTuning() });
            foreach (MechanicCodexEntry e in MechanicCodex.Entries.Where(x => x.Tab == MechanicCodex.TabReaction).ToList())
            {
                MechanicCodex.Unlock(e.Id);
            }
            string after = JsonUtility.ToJson(new ConfigBox { C = CombatSite.ConfigFromTuning() });
            Expect(before == after && MechanicCodex.UnlockedCountIn(MechanicCodex.TabReaction) == MechanicCodex.CountIn(MechanicCodex.TabReaction),
                $"F1 FGR-EMG-183：组合图鉴全部 {MechanicCodex.CountIn(MechanicCodex.TabReaction)} 条解锁后，战斗内核配置逐字段不变（只记录发现，不给数值加成）");
        }

        [Serializable]
        private sealed class ConfigBox
        {
            public CombatConfig C;
        }

        // ── G 单位悬停三段（DEBT-EMG1CHAIN01-04）─────────────────────────────

        private static void CheckUnitHover()
        {
            CampaignState s = NewState(9709);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int a = Unit(k, CombatFaction.Player, new double2(0, 0));
            BindMachine(site, a, 201);
            int foe = Unit(k, CombatFaction.Hostile, new double2(6, 0));
            StepSite(site, 1);
            string calm = EmgChainView.UnitHoverLine(site, s, foe);
            k.AddAccum(foe, EmgPhaseConst.Charge, k.PhaseRuleAt(EmgPhaseConst.Charge).Enter * 0.5f);
            string building = EmgChainView.UnitHoverLine(site, s, foe);
            string what = GameText.Get("emg.hover.label.what").Split('{')[0], who = GameText.Get("emg.hover.label.who").Split('{')[0], next = GameText.Get("emg.hover.label.next").Split('{')[0];
            Expect(calm != null && calm.Contains(what) && calm.Contains(who) && calm.Contains(next) && calm.Contains(GameText.Get("emg.hover.unit.next_none"))
                   && building.Contains("50%") && building.Contains(EmgPhaseFeedback.PhaseName(EmgPhaseConst.Charge)) && !GameText.ContainsMarker(calm + building)
                   && EmgChainView.UnitHoverLine(site, s, 99999) == null,
                $"G1 悬停单位三段：是什么 / 谁造成的 / 接下来——平静时“{calm?.Replace("\n", " | ")}”；累积电荷一半时“{building?.Replace("\n", " | ")}”；不存在的单位不出");

            // FG11-ATTR-01 ①（DEBT-EMG1CHAIN01-04 重开部分）正向：累积阶段也显示“谁造成的”；没有来源的累积写“来源不明（环境 / 斑块）”
            int f2 = Unit(k, CombatFaction.Hostile, new double2(12, 0));
            StepSite(site, 1);
            k.AddAccum(f2, EmgPhaseConst.Chill, k.PhaseRuleAt(EmgPhaseConst.Chill).Enter * 0.5f, a);
            string accWho = EmgChainView.UnitHoverLine(site, s, f2);
            string wantAcc = GameText.Format("emg.hover.unit.who_acc", EmgPhaseFeedback.AccName(EmgPhaseConst.Chill), EmgChainView.PartyName(site, s, a));
            string wantUnknownAcc = GameText.Format("emg.hover.unit.who_acc_unknown", EmgPhaseFeedback.AccName(EmgPhaseConst.Charge));
            Expect(accWho != null && accWho.Contains(wantAcc) && building.Contains(wantUnknownAcc) && !GameText.ContainsMarker(accWho),
                $"G1 正向：A 给敌人加了一半寒量 → 悬停“{accWho?.Replace("\n", " | ")}”（含“{wantAcc}”）；没有来源的电荷累积写“{wantUnknownAcc}”");

            // 负向：A 冻住它 → 解冻、免疫窗过去 → 环境（没有来源）再冻住：悬停不显示上一次相态的来源 A
            int f3 = Unit(k, CombatFaction.Hostile, new double2(18, 0));
            StepSite(site, 1);
            k.AddAccum(f3, EmgPhaseConst.Chill, k.PhaseRuleAt(EmgPhaseConst.Chill).Enter * 3f, a);
            string frozenByA = EmgChainView.UnitHoverLine(site, s, f3);
            bool inPhase(int id) => k.TryGetUnitEmg(id, out _, out EmgUnitState st) && (st.PhaseMask & (1 << EmgPhaseConst.Chill)) != 0;
            bool ready(int id) => k.TryGetUnitEmg(id, out _, out EmgUnitState st) && (st.PhaseMask & (1 << EmgPhaseConst.Chill)) == 0 && CombatLogic.PhsTOf(st, EmgPhaseConst.Chill) <= 0f;
            bool wasIn = inPhase(f3);
            for (int i = 0; i < 60 * 60 && !ready(f3); i++)
            {
                StepSite(site, 1);
            }
            bool thawed = ready(f3);
            k.AddAccum(f3, EmgPhaseConst.Chill, k.PhaseRuleAt(EmgPhaseConst.Chill).Enter * 3f);
            string frozenAgain = EmgChainView.UnitHoverLine(site, s, f3);
            string wantA = GameText.Format("emg.hover.unit.who_phase", EmgPhaseFeedback.PhaseName(EmgPhaseConst.Chill), EmgChainView.PartyName(site, s, a));
            string wantUnknown = GameText.Format("emg.hover.unit.who_phase_unknown", EmgPhaseFeedback.PhaseName(EmgPhaseConst.Chill));
            Expect(wasIn && thawed && inPhase(f3) && frozenByA.Contains(wantA) && frozenAgain.Contains(wantUnknown) && !frozenAgain.Contains(wantA),
                $"G1 负向：A 冻住时“{frozenByA?.Replace("\n", " | ")}”；解冻 {thawed}、环境再冻住后“{frozenAgain?.Replace("\n", " | ")}”——不显示上一次的来源 A");

            // 负向（FG11-ATTR-01 审查修复）：A 只加了一半寒量、没进相态 → 自然衰减到 0（之后单位全零，StepPhase 开头提前返回）→ 环境再累积 / 再冻住：都不显示 A
            int f4 = Unit(k, CombatFaction.Hostile, new double2(24, 0));
            StepSite(site, 1);
            k.AddAccum(f4, EmgPhaseConst.Chill, k.PhaseRuleAt(EmgPhaseConst.Chill).Enter * 0.5f, a);
            bool srcA = k.TryGetPhaseSource(f4, EmgPhaseConst.Chill, out int src0) && src0 == a;
            bool drained(int id) => k.TryGetUnitEmg(id, out _, out EmgUnitState st) && CombatLogic.AccOf(st, EmgPhaseConst.Chill) == 0f && CombatLogic.PhsTOf(st, EmgPhaseConst.Chill) == 0f;
            int steps = 0;
            for (; steps < 60 * 60 && !drained(f4); steps++)
            {
                StepSite(site, 1);
            }
            bool decayed = drained(f4) && !inPhase(f4);
            StepSite(site, 30); // 全零后再走几步：走的是提前返回
            k.TryGetPhaseSource(f4, EmgPhaseConst.Chill, out int srcAfter);
            k.AddAccum(f4, EmgPhaseConst.Chill, k.PhaseRuleAt(EmgPhaseConst.Chill).Enter * 0.5f);
            string envAcc = EmgChainView.UnitHoverLine(site, s, f4);
            string wantUnknownChill = GameText.Format("emg.hover.unit.who_acc_unknown", EmgPhaseFeedback.AccName(EmgPhaseConst.Chill));
            string wantAccA = GameText.Format("emg.hover.unit.who_acc", EmgPhaseFeedback.AccName(EmgPhaseConst.Chill), EmgChainView.PartyName(site, s, a));
            k.AddAccum(f4, EmgPhaseConst.Chill, k.PhaseRuleAt(EmgPhaseConst.Chill).Enter * 3f);
            string envFrozen = EmgChainView.UnitHoverLine(site, s, f4);
            k.TryGetPhaseSource(f4, EmgPhaseConst.Chill, out int srcFrozen);
            Expect(srcA && decayed && srcAfter == 0 && envAcc != null && envAcc.Contains(wantUnknownChill) && !envAcc.Contains(wantAccA)
                   && inPhase(f4) && srcFrozen == 0 && envFrozen.Contains(wantUnknown) && !envFrozen.Contains(wantA),
                $"G1 负向（累积衰减）：A 加一半寒量（来源 {src0}）→ {steps} 步衰减到 0、再空走 30 步后来源 {srcAfter}；环境再累积“{envAcc?.Replace("\n", " | ")}”、再冻住“{envFrozen?.Replace("\n", " | ")}”——都不显示 A");
        }

        // ── H 图鉴里可查看敌方单位的程序（FGR-EMG-162，EMG4-HIJ-01 落地）─────────

        private static void CheckEnemyProgram()
        {
            NewState(9710);
            GameConfig.fg.MechEnemy shown = EnemyDefs.Rows.FirstOrDefault(r => CombatSite.EnemyProgramText(r) != null);
            string body = shown != null ? MechanicCodex.Body(MechanicCodex.Find(MechanicCodex.EnemyEntryId(shown.Id)), null) : string.Empty;
            Expect(shown != null && body.Contains(CombatSite.EnemyProgramText(shown)),
                $"H1 FGR-EMG-162：敌人图鉴条目带敌方程序（读写端口）——{shown?.Id}");
        }

        // ── I 面板：空状态、◆、256 条可用、键鼠、布局探针 ───────────────────────

        private static void CheckPanel()
        {
            FreshCodex("panel");
            CampaignState s = NewState(9711);
            VisualElement root = MountUxml(UiKitFolder + "MechanicCodexPanel.uxml", out GameObject go);
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = true;
            FieldInfo entriesField = typeof(MechanicCodex).GetField("_entries", BindingFlags.Static | BindingFlags.NonPublic);
            FieldInfo byIdField = typeof(MechanicCodex).GetField("_byId", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                var panel = go.AddComponent<MechanicCodexPanelUIToolkit>();
                panel.BindView(root);
                panel.OpenAt(null);
                panel.SelectTab(MechanicCodex.TabReaction);
                panel.Refresh();
                bool empty = panel.NoMatchVisible && panel.ItemCount == MechanicCodex.CountIn(MechanicCodex.TabReaction);
                ReactionFeedback.RecordFirst(s, "reaction_deflagrate", SiteId);
                ReactionDiscoveryService.OnExtreme(s, "reaction_deflagrate", SiteId);
                panel.Select(MechanicCodex.ReactionEntryId("reaction_deflagrate"));
                panel.Refresh();
                int idx = Enumerable.Range(0, panel.ItemCount).FirstOrDefault(i => panel.ItemId(i) == MechanicCodex.ReactionEntryId("reaction_deflagrate"));
                bool shown = !panel.NoMatchVisible && panel.ItemText(idx).StartsWith("◆ ", StringComparison.Ordinal) && panel.EntryBodyText.Contains("◆") && panel.TabText(Array.IndexOf(MechanicCodex.Tabs, MechanicCodex.TabReaction)).StartsWith(GameText.Get("codex.tab.reaction"), StringComparison.Ordinal);
                Expect(empty && shown,
                    $"I1 面板：第一次打开组合图鉴页签显示空状态提示（{empty}）；发现并记极限后空状态消失、列表条目“{panel.ItemText(idx)}”、正文带极限行；页签名“{GameText.Get("codex.tab.reaction")}”（{MechanicCodex.ComboUiId}）");

                // B12：规则表满 256 行时——临时塞进 256 条具名交互条目（反射，测完 Reload 还原），列表、搜索、选中都可用且有界
                var entries = (List<MechanicCodexEntry>)entriesField.GetValue(null);
                var byId = (Dictionary<string, MechanicCodexEntry>)byIdField.GetValue(null);
                for (int i = 0; i < 256; i++)
                {
                    var e = new MechanicCodexEntry { Kind = MechanicCodexKind.Reaction, ContentId = "reaction_fake_" + i, Id = MechanicCodex.ReactionEntryId("reaction_fake_" + i), Tab = MechanicCodex.TabReaction, SortOrder = 250000 + i };
                    entries.Add(e);
                    byId[e.Id] = e;
                }
                MechanicCodex.Touch(); // 版本递增：面板重建
                var sw = Stopwatch.StartNew();
                panel.Refresh();
                double ms = sw.Elapsed.TotalMilliseconds;
                int total = panel.ItemCount;
                panel.SetSearch(NamedReactionCatalog.NameOf("reaction_deflagrate"));
                panel.Refresh();
                int filtered = panel.ItemCount;
                Button pick = panel.ItemButton(0);
                Click(pick);
                bool selected = panel.SelectedId == MechanicCodex.ReactionEntryId("reaction_deflagrate");
                panel.SetSearch(string.Empty);
                panel.Refresh();
                Expect(total == MechanicCodex.CountIn(MechanicCodex.TabReaction) && total >= 256 + 29 + 37 && filtered >= 1 && filtered < 5 && selected && ms < 200.0 && pick.focusable,
                    $"I2 B12：组合图鉴 {total} 条（含 256 条满表条目 + 语法条目）一次重建 {ms:0.0} ms；按名字搜索剩 {filtered} 条，鼠标点中选中；条目是可聚焦按钮（键盘 Tab / 回车可达，B02）");
            }
            finally
            {
                MechanicCodex.Reload();
                MechanicCodexPanelUIToolkit.Close();
                Object.DestroyImmediate(go);
            }

            string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "MechanicCodexPanel.uxml", "CodexWindow", stressFill: true, prepare: r =>
            {
                var probeGo = new GameObject("__probe_codex_emg4") { hideFlags = HideFlags.HideAndDontSave };
                MechanicCodexPanelUIToolkit p = probeGo.AddComponent<MechanicCodexPanelUIToolkit>();
                p.BindView(r.panel.visualTree);
                p.OpenAt(MechanicCodex.ReactionEntryId("reaction_deflagrate"));
                p.SetOpen(false);
                Object.DestroyImmediate(probeGo);
                r.panel.visualTree.Q<VisualElement>("CodexRoot")?.RemoveFromClassList("uk-hidden");
            });
            Expect(probe.StartsWith("PASS", StringComparison.Ordinal), "I3 UiToolkitLayoutProbe（组合图鉴条目、四种分辨率、超长文字）：" + (probe.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : probe.Replace("\n", " | ")));
        }

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgEmgCodexSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            MethodInfo invoke = typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        // ── 报告 ───────────────────────────────────────────────────────────────

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
        }

        private static void Expect(bool ok, string text)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + text);
            }
            else
            {
                Fail(text);
            }
        }

        private static void Fail(string text)
        {
            _fail++;
            Line("  ✗ " + text);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
