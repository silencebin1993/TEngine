using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG2-DISC-01（FG19 FGR-EMG-182 / 184 / 190 / 212；FGT-EMG-062 首次发现部分、FGT-EMG-041、FGT-EMG-038）：
    /// 起真实地点（CombatSite + 内核 + 通道规则表）跑：首次发现只提示一次、20 条聚合、语法发现、旧档不补弹不改记录、交互伤害按因果链平分、归属上限、友伤不刷屏、观察与否一致、存读档。
    /// 已并入 CellFrameworkValidate.RunAll（段名 FgEmgDiscoverySelfCheck）。
    /// </summary>
    public static class FgEmgDiscoverySelfCheck
    {
        private const float Dt = 1f / 60f;
        private const string SiteId = "emg-disc-site";
        private const int Slot = 0;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("QA/自检/首次发现与交互伤害归因")]
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
            Line("\n[首次发现与交互伤害归因] FGR-EMG-182 / 184 / 190 / 212");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgdisc-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                FeedbackCues.ObservedSiteProvider = () => null; // 不观察：这段不慢放（观察中的慢放 FgReactionFeedbackSelfCheck 已测）
                Step(CheckFirstDiscovery);
                Step(CheckGrammar);
                Step(CheckAggregate);
                Step(CheckOldSave);
                Step(CheckAttribution);
                Step(CheckDepartedCredit);
                Step(CheckOwnersCap);
                Step(CheckAttrDot);
                Step(CheckAttrUnitLayer);
                Step(CheckAttrPersist);
                Step(CheckAttrNoGameplay);
                Step(CheckAttrSpeed);
                Step(CheckAttrHome);
                Step(CheckFriendly);
                Step(CheckObservation);
                Step(CheckSaveRoundTrip);
                Step(CheckDeterminismAfterLoad);
                Step(CheckUi);
                Step(CheckPerf);
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
            Line($"  · [首次发现与交互伤害归因] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ───────────────────────────────────────────────────────────────

        private static CampaignState NewState(int seed)
        {
            GameClock.ResetSession();
            NotificationCenter.ResetForTests();
            ReactionDiscoveryService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgdisc-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            NotificationCenter.Bind(s);
            // 地点有一次出击记录 = 有一场远征（ReactionAttribution.Current）
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

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon = -1, float hp = 100000f, CombatUnitKind? kind = null) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = kind ?? (f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy), Faction = f,
            Behavior = CombatBehavior.None, Flags = Live, Position = p, Home = p, Radius = 0.5f, Speed = 0f, Health = hp, MaxHealth = hp,
            Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f,
            ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
        });

        /// <summary>自检造地点时直接登记“单位 → 机器”（正式流程 AttachMachine 要完整的机器记录与蓝图，归因只用这张映射）。</summary>
        private static void BindMachine(CombatSite site, int unit, int logicId)
        {
            var map = (Dictionary<int, int>)typeof(CombatSite).GetField("_unitMachine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(site);
            map[unit] = logicId;
        }

        /// <summary>EMGR-GL-01：同时登记“机器 → 单位”，让正式的 <see cref="CombatSite.RemoveMachine"/>（撤离 / 阵亡后清理）能注销它。</summary>
        private static void BindMachineFull(CombatSite site, int unit, int logicId)
        {
            BindMachine(site, unit, logicId);
            var map = (Dictionary<int, int>)typeof(CombatSite).GetField("_machineUnit", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(site);
            map[logicId] = unit;
        }

        private static ReactionSessionRecord Session(CampaignState s) => ReactionAttribution.Current(s, SiteId, create: false);

        private static List<NotificationEntry> Notes(string type) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == type).ToList();

        /// <summary>明火（归属 A）紧贴一块油（归属 B）：热交换把油点着 → 爆燃。敌人站在油里。</summary>
        private static void Ignite(CombatKernel k, double y, int ownerFire, int ownerOil, float life = 8f)
        {
            k.SpawnPatch(EmgMatterConst.Fire, new double2(0, y), 2f, 1f, life, ownerFire, false, 7);
            k.SpawnPatch(EmgMatterConst.Oil, new double2(3, y), 2f, 5f, 0f, ownerOil, false, 0);
        }

        // ── A 首次发现 ─────────────────────────────────────────────────────────

        private static void CheckFirstDiscovery()
        {
            CampaignState s = NewState(9601);
            int hooks = 0;
            void OnHook(string id) => hooks += id == GuidanceHooks.EmgFirstDiscovery ? 1 : 0;
            GuidanceHooks.Touched += OnHook;
            try
            {
                using var site = NewSite();
                Ignite(site.Kernel, 0, 0, 0);
                bool before = !ReactionFeedback.IsFirstTriggered(s, "reaction_deflagrate") && !NamedReactionCatalog.WasAnnouncedByOldBatch(s, "reaction_deflagrate");
                StepSite(site, 300);
                int count = site.Kernel.EmgRuleCountOfKey(NamedReactionCatalog.StableKey("reaction_deflagrate"));
                ReactionFirstTriggerRecord rec = ReactionFeedback.FirstRecordOf(s, "reaction_deflagrate");
                List<NotificationEntry> notes = Notes(ReactionDiscoveryService.NotifyType);
                string text = notes.FirstOrDefault()?.Latest?.DetailText ?? string.Empty;
                bool codex = MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId("reaction_deflagrate"));
                bool level = notes.Count == 1 && notes[0].Level == NotifyLevel.Info && notes[0].Latest.HasLocation;
                Expect(before && count >= 1 && rec != null && rec.SiteId == SiteId && codex && level && text.Contains(GameText.Get("reaction.deflagrate.name")) && hooks >= 1
                       && (s.OpenReactionBatches?.Length ?? 0) == 0 && Session(s).NewDiscoveries == 1,
                    $"A1 FGT-EMG-062：第一次结算爆燃（第一幕，没有任何批次开放）→ 报名字、进图鉴（{codex}）、首次记录（{rec?.SiteId}）、信息级可定位“新发现”（“{text}”）、钩子 emg_first_discovery（{hooks}）、本场新发现 {Session(s).NewDiscoveries}");

                int posted = ReactionDiscoveryService.Posted;
                int records = s.ReactionFirstTriggers.Length;
                Ignite(site.Kernel, 30, 0, 0);
                StepSite(site, 300);
                int count2 = site.Kernel.EmgRuleCountOfKey(NamedReactionCatalog.StableKey("reaction_deflagrate"));
                ReactionShareRecord row = Session(s).Reactions.FirstOrDefault(r => r.ReactionId == "reaction_deflagrate");
                Expect(count2 > count && ReactionDiscoveryService.Posted == posted && s.ReactionFirstTriggers.Length == records && row != null && row.Count == count2 && Session(s).NewDiscoveries == 1,
                    $"A2 负向：同一交互第二次只计数（{count} → {count2}，日志行 {row?.Count} 次），不再提示、不再写记录、本场新发现仍是 1");
                Expect(GuidanceHooks.Known.Contains(GuidanceHooks.EmgFirstDiscovery) && GuidanceHooks.EmgFirstDiscovery == "emg_first_discovery" && !string.IsNullOrEmpty(ReactionDiscoveryService.TipOf(RowOf("reaction_deflagrate"))),
                    "A3 引导钩子已登记；图鉴打法提示 reaction.<id>.tip 保留");
            }
            finally
            {
                GuidanceHooks.Touched -= OnHook;
            }
        }

        private static GameConfig.fg.Reaction RowOf(string id) => NamedReactionCatalog.TryGet(id, out GameConfig.fg.Reaction r) ? r : null;

        // ── B 语法发现 ─────────────────────────────────────────────────────────

        private static void CheckGrammar()
        {
            CampaignState s = NewState(9602);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_split", "fw_marktag" }, FirmwareCarrier.Melee);
            var w = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 3f, Damage = 10f, Cooldown = 0f };
            w.Reading = CarrierReadings.Build(ComponentCatalog.CompRamId, null, null, Array.Empty<string>(), false);
            w.Reading = CombatSite.FoldProgramReadings(w.Reading, pr, false);
            w.Program = CombatSite.RegisterProgram(k, pr);
            int me = Unit(k, CombatFaction.Player, new double2(0, -1.5), k.AddWeapon(w));
            int t = Unit(k, CombatFaction.Hostile, double2.zero);
            Unit(k, CombatFaction.Hostile, new double2(0, 2.5));
            StepSite(site, 1);
            bool empty = (s.GrammarDiscoveries?.Length ?? 0) == 0;
            k.FireAt(me, t, k.Time);
            site.ProcessEvents();
            StepSite(site, 60);
            string[] ids = (s.GrammarDiscoveries ?? Array.Empty<GrammarDiscoveryRecord>()).Select(r => r.EntryId).ToArray();
            bool gen = ReactionDiscoveryService.IsGrammarDiscovered(s, ReactionDiscoveryService.GenEntry(1)) == (k.EmgBusStats.Splits > 0);
            bool evt = ids.Any(i => i.StartsWith("evt.", StringComparison.Ordinal));
            int posted = ReactionDiscoveryService.Posted;
            int n0 = ids.Length;
            k.FireAt(me, t, k.Time);
            site.ProcessEvents();
            StepSite(site, 60);
            bool once = s.GrammarDiscoveries.Length == n0 && ReactionDiscoveryService.Posted == posted;
            string text = n0 > 0 ? ReactionDiscoveryService.GrammarText(ids[0]) : string.Empty;
            int depth = Session(s)?.MaxChainDepth ?? 0;
            Expect(empty && k.EmgBusStats.Splits > 0 && gen && evt && once && depth >= 1 && !GameText.ContainsMarker(text),
                $"B1 FGR-EMG-183：组成子弹药 + 触发事件写语法发现（{string.Join(",", ids)}，文本“{text}”，子弹药 {k.EmgBusStats.Splits}），场次最长链深 {depth}；再来一次不重复记、不重复提示（{once}）");
        }

        // ── C 聚合 ─────────────────────────────────────────────────────────────

        private static void CheckAggregate()
        {
            CampaignState s = NewState(9603);
            string[] named = NamedReactionCatalog.Rows.Where(r => r != null && r.Reach == NamedReactionCatalog.Reachable).Select(r => r.Id).ToArray();
            int total = 0;
            foreach (string id in named.Take(16))
            {
                total += ReactionFeedback.RecordFirst(s, id, SiteId) ? 1 : 0;
            }
            for (int g = 1; g <= 2; g++)
            {
                total += ReactionDiscoveryService.RecordGrammar(s, ReactionDiscoveryService.GenEntry(g), SiteId) ? 1 : 0;
            }
            for (int e = 1; total < 20; e++)
            {
                total += ReactionDiscoveryService.RecordGrammar(s, ReactionDiscoveryService.EventEntry(e), SiteId) ? 1 : 0;
            }
            List<NotificationEntry> notes = Notes(ReactionDiscoveryService.NotifyType);
            Expect(total == 20 && notes.Count == 1 && notes[0].Count == 20 && ReactionAttribution.Current(s, SiteId, create: false)?.NewDiscoveries == 20,
                $"C1 负向：一场里新发现 20 条 → 通知聚合成 1 条（{notes.Count} 条，计 {notes.FirstOrDefault()?.Count} 次，标题“{notes.FirstOrDefault()?.Text}”），本场新发现 20");
        }

        // ── D 旧档（FGT-EMG-041）──────────────────────────────────────────────

        [Serializable]
        private sealed class TestEnvelope
        {
            public int SchemaVersion;
            public int ContentVersion;
            public string Checksum;
            public string WrittenAtUtc;
            public string ProductVersion;
            public string CardJson;
            public string PayloadJson;
        }

        private static void CheckOldSave()
        {
            CampaignState s = NewState(9604);
            s.OpenReactionBatches = new[] { "clarity" };                   // 旧档：化工批已开放（当时通知过名字），爆燃从未触发
            ReactionFeedback.RecordFirst(s, "reaction_conduct", SiteId);   // 旧档已有一条首次记录
            NotificationCenter.ResetForTests();
            NotificationCenter.Bind(s);
            ReactionDiscoveryService.ResetForTests();
            ReactionSessionRecord live = ReactionAttribution.Current(s, SiteId, create: true);
            live.TotalDamage = 100;
            string firstJson = JsonUtility.ToJson(new FirstWrap { Items = s.ReactionFirstTriggers });
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            // 旧格式 v2：真实存档文件去掉本 Story 新增的全部字段（语法发现、场次的交互统计）
            string strip = RewriteAsOldSave(Slot, out int schemaOnDisk);
            if (strip != null)
            {
                Fail("D 准备：" + strip);
            }
            LoadResult loaded = CampaignSaveService.Load(Slot);
            CampaignState l = loaded.State;
            if (!saved.Success || !loaded.Success || l == null)
            {
                Fail($"D 准备：存档 {saved.Success} / 读档 {loaded.Success}（{loaded.Message}）");
                return;
            }
            CampaignFgStateDomains.EnsureAll(l);
            string firstBack = JsonUtility.ToJson(new FirstWrap { Items = l.ReactionFirstTriggers });
            ReactionSessionRecord sess = l.Stats.ReactionSessions.FirstOrDefault(r => r.SiteId == SiteId);
            bool defaults = (l.GrammarDiscoveries?.Length ?? 0) == 0 && firstBack == firstJson && sess != null && sess.InteractionCount == 0 && sess.InteractionDamage == 0
                            && sess.MaxChainDepth == 0 && sess.NewDiscoveries == 0 && (sess.Credits?.Length ?? 0) == 0;
            bool schema = schemaOnDisk == 2 && CampaignSaveService.CurrentSchemaVersion == 2 && CampaignSaveMigrations.Production.TargetVersion == 2 && CampaignSaveMigrations.Production.MinVersion == 2;
            bool quiet = ReactionDiscoveryService.Posted == 0;
            Expect(defaults && schema && quiet && (l.OpenReactionBatches ?? Array.Empty<string>()).Contains("clarity"),
                $"D1 FGT-EMG-041：v2 旧档读入 schema 仍 2、迁移链不登记升级器（{schema}）；新字段取缺省、ReactionFirstTriggers 原样（{defaults}）；读档本身不弹“新发现”（{quiet}）");

            CampaignSession.Set(Slot, l);
            int slow = GameClock.SlowMotionStarts;
            int recorded = ReactionFeedback.FirstTriggersRecorded;
            bool first = ReactionFeedback.RecordFirst(l, "reaction_deflagrate", SiteId);
            bool firstNewBatch = ReactionFeedback.RecordFirst(l, "reaction_electrolysis", SiteId);
            int posted = ReactionDiscoveryService.Posted;
            Expect(first && firstNewBatch && ReactionFeedback.FirstRecordOf(l, "reaction_deflagrate") != null && posted == 1 && ReactionFeedback.FirstTriggersRecorded == recorded + 2,
                $"D2 负向：已开放但从未触发的爆燃（旧档已通知过）第一次结算写发现记录、不重复弹提示；旧档没开放的电解照常提示（共提示 {posted} 条，应 1）");
            Expect(!ReactionFeedback.RecordFirst(l, "reaction_conduct", SiteId), "D3 旧档已有首次记录的反应不算新发现");

            // 内核快照（FGR-EMG-211）：本 Story 给斑块段 / 待生成段追加的字段，短段读入取缺省；FormatVersion 保持 17（同阶段自描述段只追加，ADR ⑩）
            using CombatKernel a = NewSite().Kernel;
            int ownerA = Unit(a, CombatFaction.Player, new double2(50, 50));
            a.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 5f, 0f, ownerA);
            byte[] old = a.SerializeEmgShapeForTests(EmgConst.SegPatches, 40, 0);
            using CombatKernel b = NewSite().Kernel;
            CombatLoadResult r = b.Load(old);
            CombatZone z = default;
            bool any = false;
            for (int i = 0; i < b.PatchSlots && !any; i++)
            {
                any = b.TryGetPatch(i, out z) && z.Matter == EmgMatterConst.Oil;
            }
            Expect(CombatConst.FormatVersion >= 18 && r == CombatLoadResult.Ok && any && z.RuleKey == 0 && z.Owners[0] == ownerA,
                $"D4 快照斑块段 40 字段（本 Story 之前）读入：规则键取 0、归属保留（{r}）；FormatVersion = {CombatConst.FormatVersion}");
        }

        /// <summary>把槽位存档文件改写成本 Story 之前的样子（去掉新增字段，校验和重算）。返回错误说明，成功返回 null。</summary>
        internal static string RewriteAsOldSave(int slot, out int schemaOnDisk)
        {
            string path = CampaignSaveService.SlotPath(slot);
            TestEnvelope env = JsonUtility.FromJson<TestEnvelope>(File.ReadAllText(path));
            schemaOnDisk = env.SchemaVersion;
            string payload = env.PayloadJson;
            foreach (string field in new[] { "GrammarDiscoveries", "InteractionCount", "InteractionDamage", "MaxChainDepth", "NewDiscoveries", "Credits" })
            {
                payload = StripField(payload, field);
                if (payload.Contains("\"" + field + "\""))
                {
                    return $"字段 {field} 没能从旧档里去掉";
                }
            }
            env.PayloadJson = payload;
            env.Checksum = CampaignSaveService.ComputeChecksum(env.PayloadJson, env.CardJson);
            File.WriteAllText(path, JsonUtility.ToJson(env));
            return null;
        }

        [Serializable]
        private sealed class FirstWrap
        {
            public ReactionFirstTriggerRecord[] Items;
        }

        /// <summary>从 JSON 文本里删掉所有名为 <paramref name="field"/> 的字段（值可以是标量或数组；存档字段名唯一，够用）。</summary>
        private static string StripField(string json, string field)
        {
            string key = "\"" + field + "\":";
            int at;
            while ((at = json.IndexOf(key, StringComparison.Ordinal)) >= 0)
            {
                int end = at + key.Length;
                if (json[end] == '[' || json[end] == '{')
                {
                    int depth = 0;
                    bool str = false;
                    for (; end < json.Length; end++)
                    {
                        char c = json[end];
                        if (str)
                        {
                            if (c == '\\')
                            {
                                end++;
                            }
                            else if (c == '"')
                            {
                                str = false;
                            }
                        }
                        else if (c == '"')
                        {
                            str = true;
                        }
                        else if (c == '[' || c == '{')
                        {
                            depth++;
                        }
                        else if ((c == ']' || c == '}') && --depth == 0)
                        {
                            end++;
                            break;
                        }
                    }
                }
                else
                {
                    while (json[end] != ',' && json[end] != '}')
                    {
                        end++;
                    }
                }
                if (json[end] == ',')
                {
                    end++;
                }
                else if (json[at - 1] == ',')
                {
                    at--;
                }
                json = json.Remove(at, end - at);
            }
            return json;
        }

        // ── E 交互伤害归因（FGT-EMG-038）───────────────────────────────────────

        /// <summary>E3b（EMGR-GL-01，DEBT-EMG2DISC01-04 前半，FGR-EMG-182）：已撤离 / 阵亡的机器留下的斑块之后造成的伤害仍分给它；同一机器换单位 ID 回来按 LogicId 去重。</summary>
        private static void CheckDepartedCredit()
        {
            CampaignState s = NewState(9611);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int a = Unit(k, CombatFaction.Player, new double2(40, 0));
            int b = Unit(k, CombatFaction.Player, new double2(40, 10));
            BindMachineFull(site, a, 201);
            BindMachineFull(site, b, 202);
            Unit(k, CombatFaction.Hostile, new double2(3, 0));
            StepSite(site, 1);
            // A 铺油后撤离（正式注销入口 RemoveMachine），B 再点火
            k.SpawnPatch(EmgMatterConst.Oil, new double2(3, 0), 2f, 5f, 0f, a, false, 0);
            site.RemoveMachine(201, export: false);
            bool gone = !site.TryGetMachineOfUnit(a, out _) && !site.IsPlayerUnit(a) && site.TryGetCreditMachine(a, out int credA) && credA == 201;
            k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 8f, b, false, 7);
            StepSite(site, 300);
            ReactionSessionRecord sess = Session(s);
            double cred(ReactionSessionRecord r, int logic) => r?.Credits?.FirstOrDefault(c => c.LogicId == logic)?.Damage ?? 0;
            Expect(gone && sess != null && cred(sess, 201) > 0 && cred(sess, 202) > 0,
                $"E3b A 铺油后撤离、B 点火：A 仍分到 {cred(sess, 201):0.0}、B {cred(sess, 202):0.0}；撤离后 A 的单位不再算在场（不是玩家单位、悬停 / 反馈查不到），只有归因查得到 {gone}");

            // 同一台机器换单位 ID 回来：归属里旧、新两个单位 + B → 按 LogicId 去重，两台各一半（不是三份）
            int a2 = Unit(k, CombatFaction.Player, new double2(40, 20));
            BindMachineFull(site, a2, 201);
            CampaignState s2 = NewState(9612);
            ReactionAttribution.Current(s2, SiteId, create: true);
            int key = NamedReactionCatalog.StableKey("reaction_deflagrate");
            ReactionAttribution.AddInteractionDamage(s2, site, new EmgAttrRow { Key = key, Owners = EmgOwners.Of(a, a2, b), Damage = 90 });
            ReactionSessionRecord sess2 = ReactionAttribution.Current(s2, SiteId, create: false);
            Expect(sess2 != null && sess2.Credits.Length == 2 && Math.Abs(cred(sess2, 201) - 45) < 1e-9 && Math.Abs(cred(sess2, 202) - 45) < 1e-9
                   && Math.Abs(sess2.Credits.Sum(c => c.Damage) - 90) < 1e-9,
                $"E3b 同一机器换单位 ID 回来（旧 {a} / 新 {a2}）+ B 共 90 伤害：去重后两台各 {cred(sess2, 201)} / {cred(sess2, 202)}，合计守恒、不重复计");

            // 机器阵亡当步斑块结算：C 铺的油在 C 被击毁的那一步照样烧敌人并分给 C；之后清理注销（RemoveMachine）也照样分
            // （新地点，与上面同一布局：不与上一场的残留斑块混在一起）
            sess = null;
            CampaignState s3 = NewState(9613);
            using var site3 = NewSite();
            k = site3.Kernel;
            int c = Unit(k, CombatFaction.Player, new double2(40, 0));
            int b3 = Unit(k, CombatFaction.Player, new double2(40, 10));
            BindMachineFull(site3, c, 203);
            BindMachineFull(site3, b3, 202);
            Unit(k, CombatFaction.Hostile, new double2(3, 0));
            StepSite(site3, 1);
            k.SpawnPatch(EmgMatterConst.Oil, new double2(3, 0), 2f, 5f, 0f, c, false, 0);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 8f, b3, false, 7);
            sess = ReactionAttribution.Current(s3, SiteId, create: true);
            double c0 = cred(sess, 203);
            bool deadMapped = false;
            EmgOwners rowOwners = default;
            int rowsSeen = 0;
            for (int i = 0; i < 300 && cred(sess, 203) <= c0; i++)
            {
                if (i == 0)
                {
                    k.Kill(c, 0); // 阵亡：内核单位死了，记录驱动的清理还没来——映射仍在
                }
                StepSite(site3, 1);
                deadMapped = site3.TryGetCreditMachine(c, out int lc) && lc == 203;
                for (int j = 0; j < k.EmgAttrCount; j++)
                {
                    rowsSeen++;
                    rowOwners = k.EmgAttrAt(j).Owners;
                }
            }
            double cDead = cred(sess, 203);
            site3.RemoveMachine(203, export: false);
            StepSite(site3, 120);
            double cAfter = cred(sess, 203);
            Expect(deadMapped && cDead > c0 && cAfter > cDead,
                $"E3b 机器阵亡当步斑块结算：C 阵亡后它的油照样分给它（{c0:0.0} → {cDead:0.0}），阵亡清理注销后继续分（→ {cAfter:0.0}）；映射仍在 {deadMapped}，交互伤害行 {rowsSeen}（最后一行归属 {rowOwners}，C = {c}）");
            if (site.TryGetCreditMachine(999999, out _))
            {
                Fail("E3b 未登记的单位不应查到机器");
            }
        }

        private static void CheckAttribution()
        {
            CampaignState s = NewState(9605);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int a = Unit(k, CombatFaction.Player, new double2(40, 0));
            int b = Unit(k, CombatFaction.Player, new double2(40, 10));
            BindMachine(site, a, 101);
            BindMachine(site, b, 102);
            int e = Unit(k, CombatFaction.Hostile, new double2(3, 0));
            StepSite(site, 1);
            Ignite(k, 0, a, b);
            int key = NamedReactionCatalog.StableKey("reaction_deflagrate");
            double seenRowDamage = 0;
            EmgOwners seenOwners = default;
            int keyRows = 0, keyRowsBoth = 0;
            for (int i = 0; i < 300; i++)
            {
                StepSite(site, 1);
                for (int j = 0; j < k.EmgAttrCount; j++)
                {
                    EmgAttrRow row = k.EmgAttrAt(j);
                    if (row.Key == key)
                    {
                        seenRowDamage += row.Damage;
                        seenOwners = row.Owners;
                        keyRows++;
                        // FG11-ATTR-01（规格口径 FGR-EMG-130 / 182，删掉“只看有第二个归属的那一行”的过滤）：斑块层产物与单位身上的沾染反应，每一行都要同时归 A 和 B
                        keyRowsBoth += row.Owners.Has(a) && row.Owners.Has(b) ? 1 : 0;
                    }
                }
            }
            ReactionSessionRecord sess = Session(s);
            ReactionShareRecord share = sess?.Reactions.FirstOrDefault(r => r.ReactionId == "reaction_deflagrate");
            bool owners = keyRows > 0 && keyRowsBoth == keyRows;
            Expect(sess != null && share != null && seenRowDamage > 0 && owners && Math.Abs(share.Damage - seenRowDamage) < 1e-6,
                $"E1 A 的明火点着 B 的油（爆燃）：产物烧到敌人的伤害归“爆燃”（场次 {share?.Damage:0.0} = 内核 {seenRowDamage:0.0}），{keyRowsBoth}/{keyRows} 行都归 A、B（末行 {seenOwners}）");
            bool bounded = sess != null && sess.InteractionDamage >= (share?.Damage ?? 0) - 1e-6 && sess.InteractionDamage <= sess.TotalDamage + 1e-6;
            double cred(int logic) => sess?.Credits?.FirstOrDefault(c => c.LogicId == logic)?.Damage ?? 0;
            Expect(bounded && cred(101) > 0 && cred(102) > 0 && sess.InteractionCount >= 1 && sess.MaxChainDepth >= 1,
                $"E2 场次：交互 {sess?.InteractionCount} 次，交互伤害 {sess?.InteractionDamage:0.0} ≤ 敌方全部 {sess?.TotalDamage:0.0}，两台机器都分到（{cred(101):0.0} / {cred(102):0.0}），最长链深含规则产物（{sess?.MaxChainDepth}）");

            // 平分：两台 → 各一半；再多算一台敌人不分（只分机器）
            CampaignState s2 = NewState(9606);
            ReactionAttribution.Current(s2, SiteId, create: true);
            ReactionAttribution.AddInteractionDamage(s2, site, new EmgAttrRow { Key = key, Owners = EmgOwners.Of(a, b, e), Damage = 90 });
            ReactionSessionRecord sess2 = ReactionAttribution.Current(s2, SiteId, create: false);
            double c1 = sess2.Credits.FirstOrDefault(c => c.LogicId == 101)?.Damage ?? 0, c2 = sess2.Credits.FirstOrDefault(c => c.LogicId == 102)?.Damage ?? 0;
            Expect(Math.Abs(c1 - 45) < 1e-9 && Math.Abs(c2 - 45) < 1e-9 && sess2.Credits.Length == 2 && Math.Abs(sess2.InteractionDamage - 90) < 1e-9,
                $"E3 平分：归属两台机器 + 一个敌人，90 伤害 → 机器各 {c1} / {c2}，合计守恒");
            // 没有规则键的产物（排放的物质）：进交互伤害总量，不记到任何反应行
            int rows0 = sess2.Reactions.Length;
            ReactionAttribution.AddInteractionDamage(s2, site, new EmgAttrRow { Key = 0, Owners = EmgOwners.Of(a), Damage = 10 });
            Expect(sess2.Reactions.Length == rows0 && Math.Abs(sess2.InteractionDamage - 100) < 1e-9, "E4 规则键 0：进总量、不开反应行");
            // 没有名字的通道规则：有行、显示规则说明，不写“未知反应”
            int ruleKey = 0;
            for (int i = 0; i < k.EmgRuleCount && ruleKey == 0; i++)
            {
                int rk = k.EmgRuleAt(i).Key;
                ruleKey = NamedReactionCatalog.IdOfKey(rk) == null && EmgRuleCatalog.TextOfKey(rk) != null ? rk : 0;
            }
            string rowName = ReactionAttribution.DisplayName(s2, ReactionAttribution.RuleRowId(ruleKey));
            Expect(ruleKey != 0 && rowName == EmgRuleCatalog.TextOfKey(ruleKey) && rowName != GameText.Get("reaction.unnamed"), $"E5 没有图鉴名字的通道规则行显示“{rowName}”");
            // 同一场景跑真实的无名规则（水洼被加热沸腾，不造成伤害也要有行）：行的次数 = 内核可靠计数，面板不再写“本场没有打出反应”
            CampaignState s4 = NewState(9617);
            using var site4 = NewSite();
            CombatKernel k4 = site4.Kernel;
            int w = Unit(k4, CombatFaction.Player, new double2(40, 0));
            StepSite(site4, 1);
            k4.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 3f, 5f, 30f, w, false, 5);
            int boilKey = 0;
            for (int i = 0; i < 300 && boilKey == 0; i++)
            {
                k4.InjectPatchEnergy(new double2(0, 0), 3f, EmgConst.PortHeat, 40f, 5, w);
                StepSite(site4, 1);
                for (int j = 0; j < k4.EmgRuleCount && boilKey == 0; j++)
                {
                    int rk = k4.EmgRuleAt(j).Key;
                    boilKey = NamedReactionCatalog.IdOfKey(rk) == null && k4.EmgRuleCountOfKey(rk) > 0 ? rk : 0;
                }
            }
            StepSite(site4, 1);
            ReactionSessionRecord sess4 = Session(s4);
            ReactionShareRecord ruleRow = sess4?.Reactions?.FirstOrDefault(r => r.ReactionId == ReactionAttribution.RuleRowId(boilKey));
            var panel4 = new List<(string Text, string Cls)>();
            ReactionAttributionView.AppendSessions(s4, ReactionLogFilter.All, panel4);
            Expect(boilKey != 0 && ruleRow != null && ruleRow.Count == k4.EmgRuleCountOfKey(boilKey) && ruleRow.Count > 0 && sess4.InteractionCount >= ruleRow.Count
                   && panel4.All(l => !l.Text.Contains(GameText.Get("reaction.attr.none"))),
                $"E5b 无名规则的行记真实次数：{ReactionAttribution.DisplayName(s4, ruleRow?.ReactionId)} ×{ruleRow?.Count} = 内核 {k4.EmgRuleCountOfKey(boilKey)}，面板有交互就不写“没有打出反应”");

            // 破片：有链的按链归属（键 = 链上最近的规则），没链的归放出者；普通出手不算交互
            CampaignState s3 = NewState(9607);
            using var site3 = NewSite();
            CombatKernel k3 = site3.Kernel;
            int m = Unit(k3, CombatFaction.Player, new double2(0, 0));
            BindMachine(site3, m, 201);
            int t3 = Unit(k3, CombatFaction.Hostile, new double2(4, 0));
            StepSite(site3, 1);
            k3.SpawnFragmentForTests(new double2(1, 0), new float2(1f, 0f), m, CombatFaction.Player, 12f, 18f, 7f);
            bool fragRow = false;
            for (int i = 0; i < 60; i++)
            {
                StepSite(site3, 1);
                for (int j = 0; j < k3.EmgAttrCount; j++)
                {
                    EmgAttrRow row = k3.EmgAttrAt(j);
                    fragRow |= row.Key == 0 && row.Owners[0] == m && row.Damage > 0;
                }
            }
            Expect(fragRow && Session(s3)?.Credits.FirstOrDefault(c => c.LogicId == 201)?.Damage > 0, "E6 破片命中敌人：记交互伤害并归放出者");
            double before = Session(s3).InteractionDamage;
            k3.Damage(t3, 50f, m);
            StepSite(site3, 1);
            Expect(Math.Abs(Session(s3).InteractionDamage - before) < 1e-9 && Session(s3).TotalDamage >= 50, "E7 对照：不是规则产物的伤害（普通命中）只进敌方全部伤害，不进交互伤害");
        }

        // ── F 归属上限 ─────────────────────────────────────────────────────────

        private static void CheckOwnersCap()
        {
            // F1 FG11-ATTR-01（ADR ⑨ 更正，FGR-EMG-182 按推荐默认 8 格，DEBT-EMG2DISC01-06）：0 / 重复不记；满 8 格先挤在场的非己方机器（敌方 / 炮塔），再挤最早的己方；格满时新来的敌方 / 炮塔不进
            CampaignState s = NewState(9608);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int foe = Unit(k, CombatFaction.Hostile, new double2(90, 90));
            int turret = Unit(k, CombatFaction.Player, new double2(92, 90), kind: CombatUnitKind.Turret);
            int[] m = Enumerable.Range(0, 8).Select(i => Unit(k, CombatFaction.Player, new double2(94 + 2 * i, 90))).ToArray();
            EmgOwners o = default;
            foreach (int id in new[] { foe, 0, m[0], foe, m[1], m[2], m[3], m[4], m[5], m[6] })
            {
                o = k.EmgOwnersAddForTests(o, id);
            }
            bool full = o.Count == 8 && o[0] == foe;
            o = k.EmgOwnersAddForTests(o, m[7]);
            bool foeOut = !o.Has(foe) && Enumerable.Range(0, 8).All(q => o[q] == m[q]);
            o = k.EmgOwnersAddForTests(o, turret);
            bool turretOut = !o.Has(turret) && Enumerable.Range(0, 8).All(q => o[q] == m[q]);
            o = k.EmgOwnersAddForTests(o, 999999); // 不在场（阵亡 / 撤离的机器）按己方留着：挤掉最早的 m0
            bool oldestOut = !o.Has(m[0]) && o[7] == 999999 && Enumerable.Range(0, 7).All(q => o[q] == m[q + 1]);
            Expect(full && foeOut && turretOut && oldestOut,
                $"F1 8 格挤出：敌人 + 7 台机器满格（{full}）→ 第 8 台机器挤掉敌人（{foeOut}）→ 炮塔格满不进（{turretOut}）→ 不在场的机器挤掉最早的己方（{oldestOut}）；末态 {o}");

            int[] u = Enumerable.Range(0, 5).Select(i => Unit(k, CombatFaction.Player, new double2(3 * i, 40))).ToArray();
            for (int i = 0; i < 5; i++)
            {
                BindMachine(site, u[i], 301 + i);
            }
            Unit(k, CombatFaction.Hostile, new double2(12, 0));
            StepSite(site, 1);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 40f, u[0], false, 9);
            for (int i = 1; i < 5; i++)
            {
                k.SpawnPatch(EmgMatterConst.Oil, new double2(3 * i, 0), 2f, 5f, 0f, u[i]);
            }
            int key = NamedReactionCatalog.StableKey("reaction_deflagrate");
            EmgOwners last = default;
            for (int step = 0; step < 1500 && last.IsEmpty; step++)
            {
                StepSite(site, 1);
                for (int i = 0; i < k.PatchSlots; i++)
                {
                    if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Fire && z.RuleKey == key && Math.Abs(z.Pos.x - 12) < 1)
                    {
                        last = z.Owners;
                    }
                }
            }
            StepSite(site, 180);
            ReactionSessionRecord sess = Session(s);
            double[] cred = Enumerable.Range(0, 5).Select(i => sess?.Credits?.FirstOrDefault(c => c.LogicId == 301 + i)?.Damage ?? 0).ToArray();
            Expect(last.Count == 5 && u.All(id => last.Has(id)) && cred.All(c => c > 0),
                $"F2 链上 5 台机器（一排油依次点着，5 台漏油车）→ 最后一块明火归属 = 5 台都在（{last}，机器 {string.Join(",", u)}；不再是旧口径“最早 3 + 最近 1”）；场次里 5 台都分到（{string.Join(" / ", cred.Select(c => c.ToString("0.0")))}）");
            // 相态节点的源单位是被推进相态的受击者，来源在第二单位：受击者不算归属（一发液氮冻住多个敌人不占归属槽）
            k.EmgChainLogForTests(EmgChainNode.Phase, 5551, u[0], u[1]);
            k.EmgChainLogForTests(EmgChainNode.Phase, 5552, u[0]);
            EmgOwners phase = k.EmgChainOwners(5551), phaseNoSrc = k.EmgChainOwners(5552);
            Expect(phase[0] == u[1] && phase[1] == 0 && phaseNoSrc.IsEmpty, $"F3 相态节点归来源、不归受击者（{phase}；没有来源时 {phaseNoSrc}）");
        }

        // ── L FG11-ATTR-01 交互伤害归因补全（FGR-EMG-130 / 181 / 182，FGT-EMG-024；DEBT-EMG2DISC01-01 / -02 / -05 / -06、DEBT-EMG1CHAIN01-04）──

        private static double RowsSum(CombatKernel k, Func<EmgAttrRow, bool> pick = null)
        {
            double t = 0;
            for (int j = 0; j < k.EmgAttrCount; j++)
            {
                EmgAttrRow r = k.EmgAttrAt(j);
                t += pick == null || pick(r) ? r.Damage : 0;
            }
            return t;
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : 0f;

        private static int FireBit => math.tzcnt(NamedReactionCatalog.BitOf("Fire"));

        private static int CoatRowsOf(CombatKernel k, int unit, int bit = -1)
        {
            int n = 0;
            for (int i = 0; i < k.CoatAttrCount; i++)
            {
                n += k.CoatAttrAt(i).Unit == unit && (bit < 0 || k.CoatAttrAt(i).Bit == bit) ? 1 : 0;
            }
            return n;
        }

        /// <summary>敌人站进 <paramref name="owner"/> 的明火直到被斑块挂上燃烧位（沾染来源表有行）。</summary>
        private static bool CoatInFire(CombatSite site, int enemy, int maxSteps = 240)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                StepSite(site, 1);
                if (CoatRowsOf(site.Kernel, enemy) > 0 && site.Kernel.StatusBitSecondsLeft(enemy, FireBit) > 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>L1 残留计入、L2 重叠只记一次、L3 同一位先后两台车只算最后一台（别的来源再挂删行）、L4 阵亡当步。</summary>
        private static void CheckAttrDot()
        {
            // L1：A 的明火沾上燃烧位后离开：残留那几秒的持续伤害（含燃烧相态）全部计入交互伤害、归 A；交互伤害 = 敌人实际掉血（不多不少）
            CampaignState s = NewState(9630);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(60, 0));
                BindMachine(site, a, 101);
                int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, a);
                bool coated = CoatInFire(site, e);
                k.SetPosition(e, new double2(0, 40));
                StepSite(site, 1);
                double left = k.StatusBitSecondsLeft(e, FireBit);
                float hp0 = Hp(k, e);
                double rows = 0, rowsA = 0;
                for (int i = 0; i < 60 * 12; i++)
                {
                    StepSite(site, 1);
                    rows += RowsSum(k);
                    rowsA += RowsSum(k, r => r.Owners.Has(a));
                }
                float loss = hp0 - Hp(k, e);
                bool exact = loss > 0 && Math.Abs(rows - loss) <= 1e-3 * Math.Max(1.0, loss);
                Expect(coated && left > 0.5 && rowsA > 0 && exact && CoatRowsOf(k, e) == 0,
                    $"L1 离开明火后残留 {left:0.0} 秒：之后 12 秒掉血 {loss:0.00} = 交互伤害 {rows:0.00}（归 A {rowsA:0.00}）；位到期后沾染来源行删掉（剩 {CoatRowsOf(k, e)}）");

                // L3：同一单位同一位被两台车先后沾（A 的明火 → B 的明火）：只有一行、归最后沾上的 B；别的来源（C 的武器）再挂同一位：删行，持续伤害不再算交互
                int b = Unit(k, CombatFaction.Player, new double2(60, 10));
                int cMachine = Unit(k, CombatFaction.Player, new double2(60, 20));
                BindMachine(site, b, 102);
                int e3 = Unit(k, CombatFaction.Hostile, new double2(20, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(20, 0), 2f, 1f, 30f, a);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(40, 0), 2f, 1f, 30f, b);
                bool byA = CoatInFire(site, e3);
                EmgOwners firstOwners = byA ? k.CoatAttrAt(Enumerable.Range(0, k.CoatAttrCount).First(i => k.CoatAttrAt(i).Unit == e3)).Owners : default;
                k.SetPosition(e3, new double2(40, 0));
                bool byB = false;
                for (int i = 0; i < 240 && !byB; i++)
                {
                    StepSite(site, 1);
                    byB = CoatRowsOf(k, e3) == 1 && Enumerable.Range(0, k.CoatAttrCount).Any(q => k.CoatAttrAt(q).Unit == e3 && k.CoatAttrAt(q).Owners.Has(b) && !k.CoatAttrAt(q).Owners.Has(a));
                }
                k.SetPosition(e3, new double2(40, 60));
                StepSite(site, 1);
                k.ApplyStatus(e3, NamedReactionCatalog.BitOf("Fire"), 6f, 0f, 0f, 0f, cMachine);
                StepSite(site, 1);
                int rowsAfterWeapon = CoatRowsOf(k, e3);
                int bitSrc = k.StatusBitSourceOf(e3, FireBit);
                Expect(byA && firstOwners.Has(a) && byB && rowsAfterWeapon == 0 && bitSrc == cMachine,
                    $"L3 同一位先后被 A、B 的明火沾上：先 {firstOwners}，再换成只归 B（{byB}，一行）；C 的武器再挂同一位 → 沾染来源行删掉（剩 {rowsAfterWeapon}），这一位来源 = C（{bitSrc}）");
            }

            // L2：两块明火（A、B）重叠沾同一位：持续伤害只记一次——交互伤害合计 = 实际掉血（旧口径按块各记一次会多记）
            NewState(9631);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(60, 0));
                int b = Unit(k, CombatFaction.Player, new double2(60, 10));
                int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, a);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0.5, 0), 2f, 1f, 30f, b);
                StepSite(site, 1);
                float hp0 = Hp(k, e);
                double rows = 0;
                int maxRows = 0;
                for (int i = 0; i < 60 * 5; i++)
                {
                    StepSite(site, 1);
                    rows += RowsSum(k);
                    maxRows = Math.Max(maxRows, CoatRowsOf(k, e, FireBit));
                }
                float loss = hp0 - Hp(k, e);
                Expect(loss > 0 && Math.Abs(rows - loss) <= 1e-3 * Math.Max(1.0, loss) && maxRows == 1,
                    $"L2 两块重叠的明火沾同一位：5 秒掉血 {loss:0.00} = 交互伤害 {rows:0.00}（不重复记）；燃烧位的沾染来源始终 1 行（最多 {maxRows}）");
            }

            // L4：单位阵亡当步结算：残留燃烧的最后一拍打死它——这一拍照样计交互伤害归 A；之后沾染来源行随单位删掉
            CampaignState s4 = NewState(9632);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(60, 0));
                BindMachine(site, a, 101);
                int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, a);
                bool coated = CoatInFire(site, e);
                k.SetPosition(e, new double2(0, 40));
                k.SetHealth(e, 0.5f, 100000f, true);
                double killStepRows = 0;
                bool died = false;
                for (int i = 0; i < 60 * 4 && !died; i++)
                {
                    StepSite(site, 1);
                    died = !k.IsAlive(e);
                    killStepRows = died ? RowsSum(k, r => r.Owners.Has(a)) : 0;
                }
                StepSite(site, 1);
                double credA = Session(s4)?.Credits?.FirstOrDefault(c => c.LogicId == 101)?.Damage ?? 0;
                Expect(coated && died && killStepRows > 0 && credA > 0 && CoatRowsOf(k, e) == 0,
                    $"L4 负向：残留燃烧打死敌人的那一步照样计交互伤害（{killStepRows:0.00}，A 分到 {credA:0.00}）；阵亡后沾染来源行删掉（剩 {CoatRowsOf(k, e)}）");
            }
        }

        /// <summary>L5 B 的油被 A 点着 B 分到（单位层，FGR-EMG-130）、L6 A 点着后 B 冻过一下燃烧伤害仍归 A、L7 破片在环外仍带规则键。</summary>
        private static void CheckAttrUnitLayer()
        {
            CampaignState s = NewState(9633);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(60, 0));
                int b = Unit(k, CombatFaction.Player, new double2(60, 10));
                BindMachine(site, a, 101);
                BindMachine(site, b, 102);
                int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 5f, 0f, b);
                uint oil = NamedReactionCatalog.BitOf("Oil");
                bool oiled = false;
                for (int i = 0; i < 240 && !oiled; i++)
                {
                    StepSite(site, 1);
                    oiled = k.TryGetStatus(e, out uint m, out _, out _, out _, out _) && (m & oil) != 0u;
                }
                k.SetPosition(e, new double2(0, 40)); // 离开油（斑块层的 A 点 B 不参与：只看单位身上的沾染反应）
                StepSite(site, 1);
                int oilSrc = k.StatusBitSourceOf(e, math.tzcnt(oil));
                k.ApplyStatus(e, NamedReactionCatalog.BitOf("Fire"), 2f, 0f, 0f, 0f, a); // A 点着沾了 B 的油的敌人（C01）
                bool rowBoth = false, productBoth = false;
                for (int i = 0; i < 120; i++)
                {
                    for (int j = 0; j < k.EmgAttrCount; j++)
                    {
                        EmgAttrRow r = k.EmgAttrAt(j);
                        rowBoth |= r.Key != 0 && r.Owners.Has(a) && r.Owners.Has(b);
                    }
                    for (int q = 0; q < k.PatchSlots; q++)
                    {
                        productBoth |= k.TryGetPatch(q, out CombatZone z) && z.RuleKey != 0 && z.Owners.Has(a) && z.Owners.Has(b);
                    }
                    StepSite(site, 1);
                }
                ReactionSessionRecord sess = Session(s);
                double c1 = sess?.Credits?.FirstOrDefault(c => c.LogicId == 101)?.Damage ?? 0, c2 = sess?.Credits?.FirstOrDefault(c => c.LogicId == 102)?.Damage ?? 0;
                Expect(oiled && oilSrc == b && rowBoth && productBoth && c1 > 0 && c2 > 0,
                    $"L5 敌人身上沾了 B 的油（来源 {oilSrc}）被 A 点着：沾染反应的伤害行与产物明火都同时归 A、B（{rowBoth} / {productBoth}），两台都分到（{c1:0.0} / {c2:0.0}）");
            }

            CampaignState s6 = NewState(9634);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(60, 0));
                int b = Unit(k, CombatFaction.Player, new double2(60, 10));
                BindMachine(site, a, 101);
                BindMachine(site, b, 102);
                int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                StepSite(site, 1);
                k.AddAccum(e, EmgPhaseConst.Ignite, k.PhaseRuleAt(EmgPhaseConst.Ignite).Enter * 3f, a);
                StepSite(site, 30);
                k.AddAccum(e, EmgPhaseConst.Chill, k.PhaseRuleAt(EmgPhaseConst.Chill).Enter * 3f, b); // B 冻过一下
                k.TryGetPhaseSource(e, EmgPhaseConst.Chill, out int chillSrc);
                double burnA = 0, burnB = 0;
                bool burning = false;
                for (int i = 0; i < 120; i++)
                {
                    StepSite(site, 1);
                    burning |= k.TryGetUnitEmg(e, out _, out EmgUnitState st) && (st.PhaseMask & (1 << EmgPhaseConst.Ignite)) != 0;
                    burnA += RowsSum(k, r => r.Key == 0 && r.Owners.Has(a));
                    burnB += RowsSum(k, r => r.Owners.Has(b));
                }
                k.TryGetPhaseSource(e, EmgPhaseConst.Ignite, out int igniteSrc);
                ReactionSessionRecord sess = Session(s6);
                double c1 = sess?.Credits?.FirstOrDefault(c => c.LogicId == 101)?.Damage ?? 0, c2 = sess?.Credits?.FirstOrDefault(c => c.LogicId == 102)?.Damage ?? 0;
                Expect(burning && chillSrc == b && burnA > 0 && burnB == 0 && c1 > 0 && c2 == 0 && (igniteSrc == a || igniteSrc == 0),
                    $"L6 A 点着后 B 冻过一下（寒量来源 {chillSrc}）：燃烧伤害 {burnA:0.00} 仍归 A、B 没分到（{burnB:0.00}；场次 {c1:0.0} / {c2:0.0}）");
            }

            // L6b（FG11-ATTR-01 审查修复）：A 只点了一点（着火度没到阈值）→ 自然衰减到 0（之后单位全零，StepPhase 开头提前返回）→ 中立明火（没有来源）点着：燃烧伤害不记到 A 名下
            CampaignState s6b = NewState(9644);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(60, 0));
                BindMachine(site, a, 101);
                int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                StepSite(site, 1);
                k.AddAccum(e, EmgPhaseConst.Ignite, k.PhaseRuleAt(EmgPhaseConst.Ignite).Enter * 0.5f, a);
                k.TryGetPhaseSource(e, EmgPhaseConst.Ignite, out int src0);
                bool drained = false;
                for (int i = 0; i < 60 * 30 && !drained; i++)
                {
                    StepSite(site, 1);
                    drained = k.TryGetUnitEmg(e, out _, out EmgUnitState st) && CombatLogic.AccOf(st, EmgPhaseConst.Ignite) == 0f && CombatLogic.PhsTOf(st, EmgPhaseConst.Ignite) == 0f;
                }
                StepSite(site, 30);
                float hp0 = Hp(k, e);
                k.AddAccum(e, EmgPhaseConst.Ignite, k.PhaseRuleAt(EmgPhaseConst.Ignite).Enter * 3f);
                double burnA = 0;
                bool burning = false;
                for (int i = 0; i < 120; i++)
                {
                    StepSite(site, 1);
                    burning |= k.TryGetUnitEmg(e, out _, out EmgUnitState st) && (st.PhaseMask & (1 << EmgPhaseConst.Ignite)) != 0;
                    burnA += RowsSum(k, r => r.Owners.Has(a));
                }
                k.TryGetPhaseSource(e, EmgPhaseConst.Ignite, out int igniteSrc);
                ReactionSessionRecord sess = Session(s6b);
                double c1 = sess?.Credits?.FirstOrDefault(c => c.LogicId == 101)?.Damage ?? 0;
                Expect(src0 == a && drained && burning && Hp(k, e) < hp0 && igniteSrc == 0 && burnA == 0 && c1 == 0,
                    $"L6b A 只点了一点（来源 {src0}）→ 衰减到 0 {drained} → 中立明火点着：燃烧掉血 {hp0 - Hp(k, e):0.00}、相态来源 {igniteSrc}、记到 A 的 {burnA:0.00}（场次 {c1:0.0}）——不算 A 的交互伤害");
            }

            // L7：破片所在链的规则节点已被环覆盖（环写满 300 条别的记录）：破片伤害仍带规则键与链上归属（持久链归属表），规则行合计 = 交互伤害总量
            CampaignState s7 = NewState(9635);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(0, 30));
                int m = Unit(k, CombatFaction.Player, new double2(0, 0));
                BindMachine(site, a, 101);
                BindMachine(site, m, 201);
                Unit(k, CombatFaction.Hostile, new double2(4, 0));
                StepSite(site, 1);
                int key = NamedReactionCatalog.StableKey("reaction_deflagrate");
                int chain = k.EmgIssueChainForTests();
                k.EmgChainLogForTests(EmgChainNode.Rule, chain, a, 0, 0, key);
                for (int i = 1; i <= 300; i++)
                {
                    k.EmgChainLogForTests(EmgChainNode.Shot, chain + i, 77);
                }
                EmgOwners viaTable = k.EmgChainOwnersKey(chain, out int tableKey);
                k.SpawnFragmentForTests(new double2(1, 0), new float2(1f, 0f), m, CombatFaction.Player, 12f, 18f, 7f, chain);
                bool keyed = false;
                for (int i = 0; i < 60; i++)
                {
                    StepSite(site, 1);
                    for (int j = 0; j < k.EmgAttrCount; j++)
                    {
                        EmgAttrRow r = k.EmgAttrAt(j);
                        keyed |= r.Key == key && r.Owners.Has(a) && r.Damage > 0;
                    }
                }
                ReactionSessionRecord sess = Session(s7);
                ReactionShareRecord share = sess?.Reactions?.FirstOrDefault(r => r.ReactionId == "reaction_deflagrate");
                Expect(k.EmgChainRecordCount == EmgConst.ChainRing && tableKey == key && viaTable.Has(a) && keyed && share != null && Math.Abs(share.Damage - sess.InteractionDamage) < 1e-6,
                    $"L7 破片在环外（环 {k.EmgChainRecordCount} 条已覆盖规则节点）：持久链归属给出键 {tableKey} 与归属 {viaTable}；破片伤害记到“爆燃”行 {share?.Damage:0.0} = 交互伤害总量 {sess?.InteractionDamage:0.0}");
            }

            // L7b（审查修复）：只有规则节点与最早的出手者被挤出环、同一条链后写的记录还在环里：键与最早的出手者仍从持久表补上，规则行合计 = 交互伤害总量
            CampaignState s7b = NewState(9645);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(0, 30));
                int m = Unit(k, CombatFaction.Player, new double2(0, 0));
                BindMachine(site, a, 101);
                BindMachine(site, m, 201);
                Unit(k, CombatFaction.Hostile, new double2(4, 0));
                StepSite(site, 1);
                int key = NamedReactionCatalog.StableKey("reaction_deflagrate");
                int chain = k.EmgIssueChainForTests();
                k.EmgChainLogForTests(EmgChainNode.Rule, chain, a, 0, 0, key);
                for (int i = 1; i <= 300; i++)
                {
                    k.EmgChainLogForTests(EmgChainNode.Shot, chain + i, 77);
                }
                k.EmgChainLogForTests(EmgChainNode.Shot, chain, m); // 同链后写的记录留在环里
                EmgOwners via = k.EmgChainOwnersKey(chain, out int viaKey);
                k.SpawnFragmentForTests(new double2(1, 0), new float2(1f, 0f), m, CombatFaction.Player, 12f, 18f, 7f, chain);
                bool keyed = false;
                for (int i = 0; i < 60; i++)
                {
                    StepSite(site, 1);
                    for (int j = 0; j < k.EmgAttrCount; j++)
                    {
                        EmgAttrRow r = k.EmgAttrAt(j);
                        keyed |= r.Key == key && r.Owners.Has(a) && r.Owners.Has(m) && r.Damage > 0;
                    }
                }
                ReactionSessionRecord sess = Session(s7b);
                ReactionShareRecord share = sess?.Reactions?.FirstOrDefault(r => r.ReactionId == "reaction_deflagrate");
                Expect(viaKey == key && via.Has(a) && via.Has(m) && keyed && share != null && Math.Abs(share.Damage - sess.InteractionDamage) < 1e-6,
                    $"L7b 规则节点被挤出、同链后段还在环里：键 {viaKey}、归属 {via}（表在前、环在后合并）；破片伤害记到“爆燃”行 {share?.Damage:0.0} = 交互伤害总量 {sess?.InteractionDamage:0.0}");
            }
        }

        /// <summary>L8 新快照段往返、读档后在途沾染照样归因、L9 旧档（格式 22，没有归因段）缺省可读。</summary>
        private static void CheckAttrPersist()
        {
            NewState(9636);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int a = Unit(k, CombatFaction.Player, new double2(60, 0));
            int b = Unit(k, CombatFaction.Player, new double2(60, 10));
            int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
            StepSite(site, 1);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, a);
            bool coated = CoatInFire(site, e);
            k.SetPosition(e, new double2(0, 40));
            k.AddAccum(e, EmgPhaseConst.Chill, k.PhaseRuleAt(EmgPhaseConst.Chill).Enter * 0.5f, b);
            int chain = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Rule, chain, a, 0, 0, 4242);
            StepSite(site, 1);
            string Dump(CombatKernel q)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < q.CoatAttrCount; i++)
                {
                    EmgCoatAttr c = q.CoatAttrAt(i);
                    sb.Append($"c{c.Unit}:{c.Bit}:{c.Key}:{c.Matter}:{c.Chain}:{c.Owners};");
                }
                for (int bit = 0; bit < 32; bit++)
                {
                    sb.Append(q.StatusBitSourceOf(e, bit)).Append(',');
                }
                for (int ph = 0; ph < EmgPhaseConst.Count; ph++)
                {
                    q.TryGetPhaseSource(e, ph, out int src);
                    sb.Append('p').Append(src);
                }
                q.TryGetChainOwn(chain, out EmgChainOwn row);
                sb.Append($";ch{row.Chain}:{row.Key}:{row.Owners}");
                return sb.ToString();
            }
            byte[] data = k.Serialize();
            using var site2 = NewSite();
            CombatKernel c2 = site2.Kernel;
            CombatLoadResult r = c2.Load(data);
            string d0 = Dump(k), d1 = Dump(c2);
            c2.TryGetPhaseSource(e, EmgPhaseConst.Chill, out int chillSrc);
            bool round = r == CombatLoadResult.Ok && d0 == d1 && c2.StateHash() == k.StateHash() && c2.CoatAttrCount > 0 && chillSrc == b && c2.StatusBitSourceOf(e, FireBit) == a
                         && c2.TryGetChainOwn(chain, out EmgChainOwn own) && own.Key == 4242 && CombatKernel.PeekFormat(data) == CombatConst.FormatVersion && CombatConst.FormatVersion >= 23;
            double r0 = 0, r1 = 0;
            for (int i = 0; i < 60 * 6; i++)
            {
                StepSite(site, 1);
                StepSite(site2, 1);
                r0 += RowsSum(k, x => x.Owners.Has(a));
                r1 += RowsSum(c2, x => x.Owners.Has(a));
            }
            Expect(coated && round && r1 > 0 && Math.Abs(r0 - r1) < 1e-6 && c2.StateHash() == k.StateHash(),
                $"L8 新快照段往返（格式 {CombatConst.FormatVersion}，{r}）：沾染来源 / 状态位来源 / 相态来源 / 链归属逐字一致、StateHash 一致；读档后在途沾染照样归 A（连续 {r0:0.00} / 读档 {r1:0.00}）");

            // L9 旧档：格式 22（本卡之前）没有段 36～38、归属只有 4 格：读入 Ok，来源全空（悬停“来源不明”），之后照常结算、不报错
            k.ApplyStatus(e, NamedReactionCatalog.BitOf("Fire"), 3f, 0f, 0f, 0f, a); // 存档时身上还在烧：读档后要照常掉血
            byte[] old = k.SerializeFormatForTests(22);
            using var site3 = NewSite();
            CombatKernel c3 = site3.Kernel;
            CombatLoadResult r3 = c3.Load(old);
            c3.TryGetPhaseSource(e, EmgPhaseConst.Chill, out int oldChill);
            bool empty = r3 == CombatLoadResult.Ok && CombatKernel.PeekFormat(old) == 22 && c3.CoatAttrCount == 0 && oldChill == 0 && c3.StatusBitSourceOf(e, FireBit) == 0 && !c3.TryGetChainOwn(chain, out _);
            float hp0 = Hp(c3, e);
            StepSite(site3, 60);
            float hp1 = Hp(c3, e);
            c3.ApplyStatus(e, NamedReactionCatalog.BitOf("Fire"), 3f, 0f, 0f, 0f, b);
            int newSrc = c3.StatusBitSourceOf(e, FireBit);
            Expect(empty && hp1 < hp0 && newSrc == b,
                $"L9 旧档（格式 22）读入 {r3}：沾染来源 {c3.CoatAttrCount} 行、相态来源 {oldChill}、链归属空；读档后照常结算（还在烧，掉血 {hp0 - hp1:0.00}）；读档后新挂上的燃烧位照常登记来源 {newSrc}");
        }

        /// <summary>L10 只改统计、不改血量胜负：同一场景两份，一份每步抹掉全部归因数据，逐步比血量 / 存活 / 位置 / 状态位 / 相态，必须完全一致。</summary>
        private static void CheckAttrNoGameplay()
        {
            NewState(9637);
            using var s1 = NewSite();
            using var s2 = NewSite();
            var ids = new List<int>();
            foreach (CombatSite site in new[] { s1, s2 })
            {
                CombatKernel k = site.Kernel;
                ids.Clear();
                int a = Unit(k, CombatFaction.Player, new double2(40, 0));
                int b = Unit(k, CombatFaction.Player, new double2(40, 10));
                ids.Add(a);
                ids.Add(b);
                for (int i = 0; i < 5; i++)
                {
                    ids.Add(Unit(k, CombatFaction.Hostile, new double2(3 * i, 0), hp: 120f));
                }
                ids.Add(Unit(k, CombatFaction.Hostile, new double2(0, 20), hp: 120f));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 40f, a, false, 9);
                for (int i = 1; i < 5; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Oil, new double2(3 * i, 0), 2f, 5f, 0f, i % 2 == 0 ? a : b);
                }
                k.ApplyStatus(ids[7], NamedReactionCatalog.BitOf("Oil"), 10f, 0f, 0f, 0f, b);
                k.ApplyStatus(ids[7], NamedReactionCatalog.BitOf("Fire"), 2f, 0f, 0f, 0f, a);
                k.AddAccum(ids[3], EmgPhaseConst.Chill, 5f, b);
            }
            string State(CombatKernel k)
            {
                var sb = new StringBuilder();
                foreach (int id in ids)
                {
                    k.TryGetUnit(id, out CombatUnitView v);
                    k.TryGetStatus(id, out uint m, out _, out _, out _, out _);
                    k.TryGetUnitEmg(id, out _, out EmgUnitState st);
                    sb.Append($"{id}:{v.Alive}:{v.Health:R}:{v.Position.x:R},{v.Position.y:R}:{m:X}:{st.PhaseMask};");
                }
                return sb.ToString();
            }
            int diffAt = -1;
            double attrRows = 0;
            int deaths = 0;
            for (int i = 0; i < 60 * 15 && diffAt < 0; i++)
            {
                s2.Kernel.EmgScrambleAttrForTests();
                StepSite(s1, 1);
                StepSite(s2, 1);
                attrRows += RowsSum(s1.Kernel);
                if (State(s1.Kernel) != State(s2.Kernel))
                {
                    diffAt = i;
                }
            }
            foreach (int id in ids)
            {
                deaths += s1.Kernel.IsAlive(id) ? 0 : 1;
            }
            Expect(diffAt < 0 && attrRows > 0 && deaths > 0,
                $"L10 只改统计不改玩法：同一场景每步抹掉全部归因数据，15 秒逐步比血量 / 存活 / 位置 / 状态位 / 相态完全一致（{(diffAt < 0 ? "一致" : "第 " + diffAt + " 步分叉")}；期间交互伤害 {attrRows:0.0}、阵亡 {deaths} 个）");
        }

        /// <summary>L11 暂停与 0.5x～3x：离开明火后的残留燃烧按游戏秒计入（暂停不记不掉血、各倍速相同）。</summary>
        private static void CheckAttrSpeed()
        {
            (double Rows, double Loss, double PauseRows, bool PauseHeld) Run(float speed)
            {
                NewState(9638);
                using var site = NewSite();
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(60, 0));
                int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, a);
                CoatInFire(site, e);
                k.SetPosition(e, new double2(0, 40));
                StepSite(site, 1);
                double left0 = k.StatusBitSecondsLeft(e, FireBit);
                float hpP = Hp(k, e);
                double pauseRows = 0;
                for (int i = 0; i < 120; i++)
                {
                    site.Step(0f, k.Time);
                    pauseRows += RowsSum(k);
                }
                bool held = Math.Abs(k.StatusBitSecondsLeft(e, FireBit) - left0) < 1e-9 && Hp(k, e) == hpP && CoatRowsOf(k, e) == 1;
                float hp0 = Hp(k, e);
                double rows = 0, until = k.Time + 10.0;
                while (k.Time < until)
                {
                    site.Step(Dt * speed, k.Time + Dt * speed);
                    rows += RowsSum(k);
                }
                return (rows, hp0 - Hp(k, e), pauseRows, held);
            }
            var slow = Run(0.5f);
            var normal = Run(1f);
            var fast = Run(3f);
            bool exact((double Rows, double Loss, double PauseRows, bool PauseHeld) x) => x.Loss > 0 && Math.Abs(x.Rows - x.Loss) <= 1e-3 * Math.Max(1.0, x.Loss);
            double tick = normal.Rows / 6.0; // 残留约 3 秒 = 6 拍；倍速之间最多差一拍（到期落在步长粒度内）
            Expect(exact(slow) && exact(normal) && exact(fast) && slow.PauseRows == 0 && normal.PauseHeld && slow.PauseHeld && fast.PauseHeld
                   && Math.Abs(slow.Rows - normal.Rows) <= tick + 1e-6 && Math.Abs(fast.Rows - normal.Rows) <= tick + 1e-6,
                $"L11 暂停 120 帧：残留秒数、血量、沾染来源不变、不记交互伤害；倍速 0.5x / 1x / 3x：残留燃烧掉血 = 交互伤害（{slow.Rows:0.00} / {normal.Rows:0.00} / {fast.Rows:0.00}）");
        }

        /// <summary>L12 负向：家园平时（没有进行中的突袭）交互伤害不进任何场次（FG02:211，DEBT-EMG2DISC01-05① 按规格关闭），首次发现照常记。</summary>
        private static void CheckAttrHome()
        {
            CampaignState s = NewState(9639);
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var home = new CombatSite(GameLogic.Campaign.Regions.HomeValleyLayout.RegionId, cfg);
            int sessions = s.Stats?.ReactionSessions?.Length ?? 0;
            ReactionAttribution.AddInteractionDamage(s, home, new EmgAttrRow { Key = NamedReactionCatalog.StableKey("reaction_deflagrate"), Owners = EmgOwners.Of(1), Damage = 50 });
            bool noSession = (s.Stats?.ReactionSessions?.Length ?? 0) == sessions && ReactionAttribution.Current(s, home.SiteId, create: true) == null
                             && ReactionAttribution.KindOfSite(s, home.SiteId) == null;
            bool recorded = ReactionFeedback.RecordFirst(s, "reaction_deflagrate", home.SiteId);
            Expect(noSession && recorded, $"L12 家园平时没有突袭：交互伤害不进场次（场次 {sessions} 条不变），首次发现照常记（{recorded}）");
        }

        // ── G 友伤（FGT-EMG-038、FGR-EMG-184）──────────────────────────────────

        private static void CheckFriendly()
        {
            NewState(9609);
            long ff0 = ReactionFeedback.FriendlyPosted;
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(30, 30));
                int b = Unit(k, CombatFaction.Player, new double2(0, 0));
                site.SetUnitLabel(a, "reaction.log.machine_n", "A");
                site.SetUnitLabel(b, "reaction.log.machine_n", "B");
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 8f, a);
                double hp0 = k.TryGetUnit(b, out CombatUnitView v0) ? v0.Health : 0;
                int peak = 0;
                for (int i = 0; i < 180; i++)
                {
                    StepSite(site, 1);
                    peak = Math.Max(peak, k.EmgFriendlyCount);
                }
                double hp1 = k.TryGetUnit(b, out CombatUnitView v1) ? v1.Health : 0;
                List<NotificationEntry> notes = Notes("friendly_fire");
                NotificationEntry n = notes.FirstOrDefault();
                string text = n?.Latest?.DetailText ?? string.Empty;
                // GameClock 在这段不走（site.Step 直驱）：同一组合只发一条；世界步进下每游戏秒一条由 FgClarityReactionSelfCheck 的倍速矩阵测
                Expect(hp1 < hp0 && notes.Count == 1 && n.Level == NotifyLevel.Warning && n.HasAnyLocation && text.Contains("A") && text.Contains("B") && text.Contains(EmgMatterCatalog.NameOf(EmgMatterConst.Fire))
                       && ReactionFeedback.FriendlyPosted - ff0 == 1 && peak == 1,
                    $"G1 B 站在 A 留下的明火里掉血（{hp0:0} → {hp1:0}）：警告级可定位聚合通知（“{text}”），3 秒持续接触不刷屏（发 {ReactionFeedback.FriendlyPosted - ff0} 条，内核每步合并到 {peak} 行）");
                bool chainNode = false;
                for (int i = 0; i < k.EmgChainRecordCount; i++)
                {
                    chainNode |= k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Kind == EmgChainNode.Friendly && r.SrcUnit == b && r.Other == a;
                }
                // 链号 0（SpawnPatch 无链）不写环：有链的场景在下面 G2 断言节点
                _ = chainNode;
            }
            NewState(9610);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(30, 30));
                int b = Unit(k, CombatFaction.Player, new double2(0, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 3f, 5f, 30f, a, false, 11);
                k.InjectPatchEnergy(new double2(0, 0), 1f, EmgConst.PortElec, 2f, 11, a);
                bool energy = false, node = false;
                for (int i = 0; i < 120; i++)
                {
                    StepSite(site, 1);
                    for (int j = 0; j < k.EmgFriendlyCount; j++)
                    {
                        int3 row = k.EmgFriendlyAt(j);
                        energy |= row.x == b && row.y == a && row.z == EmgFriendlyCode.Energy;
                    }
                }
                for (int i = 0; i < k.EmgChainRecordCount; i++)
                {
                    node |= k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Kind == EmgChainNode.Friendly && r.SrcUnit == b && r.Other == a;
                }
                string text = Notes("friendly_fire").FirstOrDefault()?.Latest?.DetailText ?? string.Empty;
                Expect(energy && node && text == GameText.Format("emg.friendly.energy", EmgChainView.PartyName(site, CampaignSession.Current, b), EmgChainView.PartyName(site, CampaignSession.Current, a)),
                    $"G2 能量：A 通电的水洼电到 B → 友伤行来源 = 能量（{energy}）、因果链“友伤”节点（{node}）、通知“{text}”");
            }
            NewState(9611);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(30, 30));
                int b = Unit(k, CombatFaction.Player, new double2(0, 0));
                StepSite(site, 1);
                k.SpawnFragmentForTests(new double2(-2, 0), new float2(1f, 0f), a, CombatFaction.Neutral, 12f, 18f, 7f);
                bool frag = false;
                for (int i = 0; i < 40; i++)
                {
                    StepSite(site, 1);
                    for (int j = 0; j < k.EmgFriendlyCount; j++)
                    {
                        int3 row = k.EmgFriendlyAt(j);
                        frag |= row.x == b && row.y == a && row.z == EmgFriendlyCode.Fragment;
                    }
                }
                Expect(frag && Notes("friendly_fire").Count == 1, "G3 破片：规则放出的中立破片（归属 A）打到 B → 友伤（来源 = 破片）");
            }
            // 负向：敌人留下的物质、世界源烧己方不是友伤
            NewState(9612);
            long ff1 = ReactionFeedback.FriendlyPosted;
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int foe = Unit(k, CombatFaction.Hostile, new double2(30, 30));
                int b = Unit(k, CombatFaction.Player, new double2(0, 0));
                int c = Unit(k, CombatFaction.Player, new double2(20, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 8f, foe);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(20, 0), 2f, 1f, 8f, 0);
                double hp0 = k.TryGetUnit(b, out CombatUnitView v0) ? v0.Health : 0;
                StepSite(site, 120);
                double hp1 = k.TryGetUnit(b, out CombatUnitView v1) ? v1.Health : 0;
                Expect(hp1 < hp0 && ReactionFeedback.FriendlyPosted == ff1 && Notes("friendly_fire").Count == 0, $"G4 负向：敌人的明火 / 没有归属的明火烧己方（{hp0:0} → {hp1:0}）不发友伤通知");
                _ = c;
            }
            // 负向：敌人阵亡后留下的明火烧己方：不发通知，因果链也不写“友伤”节点（FG19 落地口径“归属里只有敌方不算友伤”）
            NewState(9618);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int foe = Unit(k, CombatFaction.Hostile, new double2(30, 30), hp: 10f);
                int b = Unit(k, CombatFaction.Player, new double2(0, 0));
                StepSite(site, 1);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 8f, foe, false, 9);
                k.Despawn(foe);
                StepSite(site, 120);
                bool gone = !k.TryGetUnit(foe, out CombatUnitView _);
                bool burned = k.TryGetUnit(b, out CombatUnitView vb) && vb.Health < vb.MaxHealth;
                bool node = false;
                for (int i = 0; i < k.EmgChainRecordCount; i++)
                {
                    node |= k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Kind == EmgChainNode.Friendly;
                }
                Expect(gone && burned && !node && Notes("friendly_fire").Count == 0, $"G5 负向：阵亡敌人（已不在场 {gone}）的明火烧己方（掉血 {burned}）：无通知、无“友伤”节点（{node}）");
            }
        }

        // ── H 观察与否一致 ─────────────────────────────────────────────────────

        private static string RunScene(bool observed, int seed)
        {
            CampaignState s = NewState(seed);
            FeedbackCues.ObservedSiteProvider = () => observed ? SiteId : null;
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int a = Unit(k, CombatFaction.Player, new double2(40, 0));
            int b = Unit(k, CombatFaction.Player, new double2(40, 10));
            BindMachine(site, a, 101);
            BindMachine(site, b, 102);
            Unit(k, CombatFaction.Hostile, new double2(3, 0));
            StepSite(site, 1);
            Ignite(k, 0, a, b);
            StepSite(site, 300);
            GameClock.CancelSlowMotion();
            FeedbackCues.ObservedSiteProvider = () => null;
            ReactionSessionRecord r = Session(s);
            string discovered = string.Join(",", s.ReactionFirstTriggers.Select(x => x.ReactionId)) + "|" + string.Join(",", (s.GrammarDiscoveries ?? Array.Empty<GrammarDiscoveryRecord>()).Select(x => x.EntryId));
            return JsonUtility.ToJson(r) + "|" + discovered;
        }

        private static void CheckObservation()
        {
            string on = RunScene(true, 9613);
            string off = RunScene(false, 9613);
            on = on.Substring(on.IndexOf("TotalDamage", StringComparison.Ordinal));
            off = off.Substring(off.IndexOf("TotalDamage", StringComparison.Ordinal));
            Expect(on == off, $"H1 FGR-BASE-021：观察中与不观察的场次统计、发现记录逐字一致（{on.Length} 字符）");
        }

        // ── I 存读档 ───────────────────────────────────────────────────────────

        private static void CheckSaveRoundTrip()
        {
            CampaignState s = NewState(9614);
            using (var site = NewSite())
            {
                CombatKernel k = site.Kernel;
                int a = Unit(k, CombatFaction.Player, new double2(40, 0));
                int b = Unit(k, CombatFaction.Player, new double2(40, 10));
                BindMachine(site, a, 101);
                BindMachine(site, b, 102);
                Unit(k, CombatFaction.Hostile, new double2(3, 0));
                StepSite(site, 1);
                Ignite(k, 0, a, b, 20f);
                int key = NamedReactionCatalog.StableKey("reaction_deflagrate");
                for (int i = 0; i < 600 && k.EmgRuleCountOfKey(key) == 0; i++)
                {
                    StepSite(site, 1);
                }
                StepSite(site, 2);
                // 内核快照：规则键与归属往返，读档后继续记账
                byte[] data = k.Serialize();
                using CombatKernel c = NewSite().Kernel;
                CombatLoadResult r = c.Load(data);
                string Dump(CombatKernel q) => string.Join(" ", Enumerable.Range(0, q.PatchSlots).Select(i => q.TryGetPatch(i, out CombatZone z) ? $"{z.EmgId}:{z.Matter}:{z.RuleKey}:{z.Owners}" : "-").OrderBy(x => x, StringComparer.Ordinal));
                string before = Dump(k), after = Dump(c);
                Expect(r == CombatLoadResult.Ok && before == after && before.Contains($":{key}:") && before.Contains($"o({a},") && c.StateHash() == k.StateHash(),
                    $"I1 内核快照往返：爆燃产物的规则键与归属逐块保留（{after}），StateHash 一致（{r}）");
            }
            ReactionDiscoveryService.RecordGrammar(s, ReactionDiscoveryService.GenEntry(1), SiteId);
            ReactionSessionRecord live = Session(s);
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(Slot);
            ReactionSessionRecord back = loaded.State?.Stats?.ReactionSessions?.FirstOrDefault(x => x.SiteId == SiteId);
            int posted = ReactionDiscoveryService.Posted;
            bool stillKnown = loaded.State != null && !ReactionDiscoveryService.RecordGrammar(loaded.State, ReactionDiscoveryService.GenEntry(1), SiteId);
            Expect(saved.Success && loaded.Success && back != null && back.InteractionCount == live.InteractionCount && Math.Abs(back.InteractionDamage - live.InteractionDamage) < 1e-6 && back.MaxChainDepth == live.MaxChainDepth
                   && back.NewDiscoveries == live.NewDiscoveries && back.Credits.Length == live.Credits.Length && Math.Abs(back.Credits[0].Damage - live.Credits[0].Damage) < 1e-6
                   && loaded.State.GrammarDiscoveries.Length == s.GrammarDiscoveries.Length && stillKnown && ReactionDiscoveryService.Posted == posted,
                $"I2 真实存档 → 读档：场次交互统计、机器分配、语法发现原样；读档后同一条不再算新发现（交互 {back?.InteractionCount} 次，伤害 {back?.InteractionDamage:0.0}）");
        }

        /// <summary>因果链环不进快照：进快照的产物归属不能从环派生，否则读档后继续跑会和不读档分叉（A 的旧链还在环里、宿主踩进 B 的来源时）。</summary>
        private static void CheckDeterminismAfterLoad()
        {
            NewState(9620);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int a = Unit(k, CombatFaction.Player, new double2(40, 0));
            int b = Unit(k, CombatFaction.Player, new double2(40, 10));
            int e = Unit(k, CombatFaction.Hostile, new double2(0, 0));
            StepSite(site, 1);
            int chain = k.EmgIssueChainForTests();
            k.EmgChainLogForTests(EmgChainNode.Rule, chain, a);
            k.TryGetUnitEmg(e, out short profile, out EmgUnitState st);
            st.LastHitChain = chain;
            k.SetUnitEmg(e, profile, st);
            k.ApplyStatus(e, NamedReactionCatalog.BitOf("Oil"), 10f, 0f, 0f, 0f, 0);
            byte[] data = k.Serialize();
            using var site2 = NewSite();
            CombatKernel c = site2.Kernel;
            CombatLoadResult r = c.Load(data);
            bool rowHasChain = false;
            foreach (CombatKernel q in new[] { k, c })
            {
                q.ApplyStatus(e, NamedReactionCatalog.BitOf("Fire"), 2f, 0f, 0f, 0f, b); // C01：沾油单位被 B 点着，脚下冒明火（OpCoatSpawn）
            }
            for (int i = 0; i < 30; i++)
            {
                StepSite(site, 1);
                StepSite(site2, 1);
                for (int j = 0; j < k.EmgAttrCount; j++)
                {
                    EmgAttrRow row = k.EmgAttrAt(j);
                    rowHasChain |= row.Owners[0] == a && row.Owners[1] == b; // 不进快照的本步伤害行可以继续用环派生的归属
                }
            }
            string Products(CombatKernel q) => string.Join(" ", Enumerable.Range(0, q.PatchSlots).Select(i => q.TryGetPatch(i, out CombatZone z) && z.RuleKey != 0 ? $"{z.EmgId}:{z.Matter}:{z.RuleKey}:{z.Owners}" : null).Where(x => x != null).OrderBy(x => x, StringComparer.Ordinal));
            string p0 = Products(k), p1 = Products(c);
            Expect(r == CombatLoadResult.Ok && p0.Length > 0 && p0 == p1 && p0.Contains($"o({b},0,") && !p0.Contains($"o({a},") && !p0.Contains($",{a},") && c.StateHash() == k.StateHash() && rowHasChain,
                $"I3 读档后继续跑含 OpCoatSpawn：产物归属只用持久数据（连续 {p0}，读档 {p1}），StateHash 一致；不进快照的伤害行仍含链上单位（{rowHasChain}）");
        }

        // ── J 界面 ─────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewState(9615);
            ReactionSessionRecord sess = ReactionAttribution.Current(s, SiteId, create: true);
            string empty = ReactionAttribution.Summary(s, sess);
            sess.TotalDamage = 200;
            sess.InteractionCount = 12;
            sess.InteractionDamage = 50;
            sess.MaxChainDepth = 2;
            sess.NewDiscoveries = 3;
            sess.Credits = new[] { new InteractionCreditRecord { LogicId = 101, Damage = 30 }, new InteractionCreditRecord { LogicId = 102, Damage = 20 } };
            ReactionAttribution.AddReaction(s, SiteId, "reaction_deflagrate", 0, 50);
            string expected = GameText.Format("reaction.attr.interaction", 12, "25", 2, 3);
            string summary = ReactionAttribution.Summary(s, sess);
            var lines = new List<(string Text, string Cls)>();
            ReactionAttributionView.AppendSessions(s, ReactionLogFilter.All, lines);
            string panel = string.Join("\n", lines.Select(l => l.Text));
            var totals = new List<(string Text, string Cls)>();
            ReactionAttributionView.AppendTotals(s, ReactionLogFilter.All, totals);
            string totalText = string.Join("\n", totals.Select(l => l.Text));
            bool marker = GameText.ContainsMarker(panel) || GameText.ContainsMarker(summary) || GameText.ContainsMarker(totalText);
            Expect(empty == GameText.Get("reaction.attr.none") && summary.Contains(expected) && panel.Contains(expected) && panel.Contains(GameText.Format("reaction.attr.credit", string.Empty)) && totalText.Contains("25") && !marker,
                $"J1 B19：统计面板、离家 / 撤离报告（Summary）显示交互次数、占比、最长链深、本场新发现（“{summary}”）；累计行按交互伤害算（“{totalText.Split('\n').ElementAtOrDefault(1)}”）；空场次写“{empty}”");
            // 旧场次（没有交互字段）照常显示，不崩
            var oldSession = new ReactionSessionRecord { Kind = ReactionAttribution.KindExpedition, SiteId = SiteId, TotalDamage = 100, Credits = null, Reactions = new[] { new ReactionShareRecord { ReactionId = "reaction_conduct", Count = 4, Damage = 10 } } };
            string oldText = ReactionAttribution.Summary(s, oldSession);
            Expect(oldText.Contains(GameText.Get("reaction.conduct.name")) && oldText.Contains("10"), $"J2 旧场次：按反应记的伤害算交互占比（“{oldText}”）");
            // 突袭：结算面板与离家报告读同一场次（占比含没有规则键的交互伤害）；场次被挤出后走记录里复制的旧显示
            CampaignState s3 = NewState(9621);
            ReactionSessionRecord raid = ReactionAttribution.BeginRaid(s3, "raid-disc");
            raid.TotalDamage = 200;
            raid.InteractionCount = 5;
            raid.InteractionDamage = 50;
            raid.NewDiscoveries = 1;
            ReactionAttribution.EndRaid(s3);
            var rec = new RaidResultRecord { Serial = 77, ReactionSessionId = raid.SessionId, ReactionTotal = 200, UnfoldTick = 0, EndTick = 10 };
            RaidResultService.EnsureState(s3).Results = new[] { rec };
            string raidLine = ReactionAttribution.InteractionLine(raid);
            string Detail()
            {
                var detail = new List<AwayLine>();
                RaidResultService.BuildDetail(s3, new RaidHistoryEntry(rec, null, rec.UnfoldTick), detail);
                return string.Join("\n", detail.Where(l => l.Section == "reaction").Select(l => l.Text));
            }
            string detailText = Detail();
            var away = new List<AwayLine>();
            RaidResultService.AppendAway(s3, 0, 20, away);
            bool awayHas = away.Any(l => l.Section == "raid" && l.Text.Contains(raidLine) && l.Action == AwayLineAction.RaidResult);
            rec.ReactionSessionId = "rs-evicted";
            string evicted = Detail();
            Expect(detailText.Contains(raidLine) && detailText.Contains(GameText.Format("raid.result.reaction_total", 25)) && !detailText.Contains(GameText.Get("raid.result.reaction_none")) && awayHas
                   && evicted.Contains(GameText.Get("raid.result.reaction_none")) && !evicted.Contains(raidLine),
                $"J3 B19 突袭：结算面板（{detailText.Replace('\n', '／')}）与离家报告都有交互四项；占比含没有规则键的伤害；场次被挤出走旧显示（{evicted.Replace('\n', '／')}）");
        }

        // ── K 性能（只测一次）──────────────────────────────────────────────────

        private static void CheckPerf()
        {
            NewState(9616);
            using var site = NewSite();
            CombatKernel k = site.Kernel;
            int[] owners = Enumerable.Range(0, 8).Select(i => Unit(k, CombatFaction.Player, new double2(200 + i * 2, 200))).ToArray();
            for (int i = 0; i < 300; i++)
            {
                Unit(k, CombatFaction.Hostile, new double2(i % 20 * 2.5, i / 20 * 2.5));
            }
            StepSite(site, 1);
            for (int i = 0; i < 64; i++)
            {
                k.SpawnPatch(EmgMatterConst.Fire, new double2(i % 8 * 6, i / 8 * 6), 3f, 1f, 30f, owners[i % owners.Length], false, 0);
            }
            StepSite(site, 5);
            int peakRows = 0;
            var sw = new Stopwatch();
            for (int i = 0; i < 120; i++)
            {
                sw.Start();
                site.Step(Dt, k.Time + Dt);
                sw.Stop();
                peakRows = Math.Max(peakRows, k.EmgAttrCount);
            }
            double ms = sw.Elapsed.TotalMilliseconds / 120.0;
            Line($"    · 性能（Editor batchmode，Burst 开）：300 敌人 + 64 块带归属明火，地点一步平均 {ms:0.###} ms（含反馈），交互伤害合并后每步最多 {peakRows} 行；真机另测");
            Expect(peakRows <= owners.Length * 2 && ms < 1.5 * 2, $"K1 交互伤害每步按“键 + 归属”合并（{peakRows} 行，不随敌人数涨），地点一步 {ms:0.###} ms");
        }

        // ── 断言 ───────────────────────────────────────────────────────────────

        private static void Step(Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                Fail($"{body.Method.Name} 抛异常：{e}");
            }
        }

        private static void Line(string text) => _report.AppendLine(text);

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("    ✓ " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            Line("    ✗ " + what);
        }
    }
}
