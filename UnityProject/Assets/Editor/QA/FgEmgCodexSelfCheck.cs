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

        [MenuItem("BinGames/QA/自检/组合图鉴与极限奖励")]
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
            Line("\n[组合图鉴与极限奖励] FGR-EMG-183 / 194 / 162（FGT-EMG-063 / 062）");
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
                Step(CheckEntries);
                Step(CheckDiscoverySnapshot);
                Step(CheckGrammar);
                Step(CheckExtreme);
                Step(CheckSaveLoad);
                Step(CheckNoBonus);
                Step(CheckUnitHover);
                Step(CheckEnemyProgram);
                Step(CheckPanel);
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
            Line($"  · [组合图鉴与极限奖励] 断言通过 {_pass}，失败 {_fail}");
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
