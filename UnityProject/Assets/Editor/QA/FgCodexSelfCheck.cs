using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
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
    /// FG9-UX-01 正式版图鉴的自动验收（FG13 FGR-UX-050 / 051、FGR-UX-030；FG09 FGR-FAC-005；FGT-UX-006、FGT-FAC-008；卡片“必须同时交付”与负向）。
    /// 全部起真实系统：真实战斗内核与地点（敌人按 fg.TbMechEnemy 生成、己方单位真开火）、真实图鉴文件（临时目录）、真实存档读写、真实叙事投放、
    /// 真 UXML 面板 + 真按键泵（UiKitInputPump）+ 真悬停提示（UiTooltip）。
    /// A 数据：16 个页签（FGR-UX-050 的 14 个 + 配方 / 研究）各有条目、条数与来源表一致、中英文标题 / 正文 / 获取提示都有且不漏文本键、互链全部可达、敌人 codexLinks 全部存在；
    /// B 首次遭遇（FGT-FAC-008）：真内核交火 → 敌人与阵营解锁、发一次“图鉴新增”；远处没交火的不解锁；被击毁 / 直接受伤同样算；同一局重复遭遇 O(1) 不重复通知；
    /// C 跨存档 / 按存档（FGT-UX-006，负向“新存档里看不到其他存档的剧情文本”）：机制条目写进图鉴文件、重启后仍在、新存档里仍在；资料库只看当前存档，
    ///   新存档里是剪影 + 获取提示、搜索不到别的存档的剧情正文、图鉴文件里没有资料库条目；真文件存读档后资料库条目读回；
    /// D 搜索与互链：已解锁按名字 / 正文、未解锁只按获取提示（不剧透名字）；状态标签 ↔ 反应 ↔ 固件、敌人 ↔ 弱点反应 / 组件（双向）、建筑 ↔ 研究、区域 ↔ 危害 / 阵营；
    /// E 悬停一键跳转（FGR-UX-051）：显式条目、按标题兜底（建筑 / 物品 / 敌人）、剧情标题不当链接；悬停提示时按图鉴键 → 打开到条目；敌人悬停读数（未遭遇不写弱点）；
    /// F 剪影与获取提示 + 面板：16 个页签按钮逐个点、未解锁“？？？” + 剪影图标 + 获取提示、解锁后显示名字 / 弱点；资料库页签读过 = 资料库已读；资料库键 Alt+J 开 / 关；布局探针；
    /// G 补解锁（进入战役 / 打开图鉴时）：已击毁的敌人、拿到过的固件能施加的状态标签、已揭开的危害、开局可建的建筑；
    /// T 时间：暂停（地点不步进）时不解锁，0.5x～3x 下同样恰好解锁一次；P 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgCodexSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgCodexSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgCodexSelfCheck
    {
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string Bench = "fg9ux-bench";
        private const int Slot = 7;
        private const int SlotB = 8;
        private const float Dt = 1f / 60f;

        private static readonly string[] SpecTabs =
        {
            MechanicCodex.TabFirmware, MechanicCodex.TabComponent, MechanicCodex.TabChassis, MechanicCodex.TabBuilding, MechanicCodex.TabItem,
            MechanicCodex.TabReaction, MechanicCodex.TabStatus, MechanicCodex.TabEnemy, MechanicCodex.TabFaction, MechanicCodex.TabRegion,
            MechanicCodex.TabEvent, MechanicCodex.TabSystem, MechanicCodex.TabHeritage, MechanicCodex.TabLore,
        };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static double _t;
        private static readonly Reader Keys = new Reader();
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 正式版图鉴")]
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
            Line("\n[正式版图鉴] 16 个页签、机制条目跨存档 / 资料库按存档、首次遭遇解锁、搜索与互链、悬停一键跳转、剪影与获取提示、资料库键（FG9-UX-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与图鉴文件）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string originalCodex = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgcodex-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                UiTuningValues.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests();
                StatusTagCatalog.Reload();
                CarrierReadings.Reload();
                NamedReactionCatalog.ResetForTests();
                FirmwareCatalog.Invalidate();
                ItemCatalog.Reload();
                HomeExplorationService.Reload();
                NarrativeCatalog.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameSettings.ResetKeyBindingsToDefault();
                Directory.CreateDirectory(_dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                FreshCodex("main");
                FirmwareLibrary.ResetForTests();
                FirmwareLibrary.Install();
                CodexHoverLink.ResetForTests();
                NarrativeService.ResetForTests();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "图鉴在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），只在遭遇 / 解锁 / 打开面板时工作，与实体数无关；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckEncounter);
                Step(CheckEncounterPaths);
                Step(CheckHoverPick);
                Step(CheckTiming);
                Step(CheckPersistence);
                Step(CheckSearchAndLinks);
                Step(CheckHoverJump);
                Step(CheckPanel);
                Step(CheckSync);
                Step(CheckRealEntryPoints);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"图鉴自检抛异常：{e}");
            }
            finally
            {
                try
                {
                    CombatSites.Close(Bench, CampaignSession.Current, dropRecord: true);
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾关闭地点抛异常：" + e.Message);
                }
                UiTooltip.ResetForTests();
                UiEscapeStack.Clear();
                UiConfirmDialog.ResetForTests();
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
                MechanicCodexPanelUIToolkit.Close();
                CodexHoverLink.ResetForTests();
                FirmwareLibrary.ResetForTests();
                NarrativeService.ResetForTests();
                NotificationCenter.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = originalCodex;
                GameSettings.ResetKeyBindingsToDefault();
                GameSettings.SetLanguage(originalLanguage);
                CampaignSession.Set(originalSlot, originalSession);
                MachineRegistry.ResetForNewCampaign();
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响结论
                }
            }
            Line($"  合计：通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        /// <summary>换一个全新的图鉴文件（等于换一台机器 / 第一次玩）。</summary>
        private static string FreshCodex(string tag)
        {
            MechanicCodex.ResetForTests();
            string path = Path.Combine(_dir, "codex_" + tag + ".json");
            MechanicCodex.FilePathOverrideForTests = path;
            MechanicCodex.Reload();
            return path;
        }

        private static CampaignState NewState(int seed, int slot = Slot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            NotificationCenter.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgcodex-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(slot, s);
            NotificationCenter.Bind(s);
            MechanicCodex.OnCampaignChanged();
            return s;
        }

        private static string T(string id) => MechanicCodex.Title(MechanicCodex.Find(id));

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            CampaignState s = NewState(9301);
            string[] tabs = MechanicCodex.Tabs;
            var missing = SpecTabs.Where(t => Array.IndexOf(tabs, t) < 0).ToList();
            var empty = tabs.Where(t => MechanicCodex.CountIn(t) == 0).ToList();
            Expect(tabs.Length == 16 && tabs.Distinct().Count() == 16 && missing.Count == 0 && empty.Count == 0 && tabs[0] == MechanicCodex.TabSystem,
                $"A1 页签 {tabs.Length} 个（FGR-UX-050 的 14 个全在 + 配方 / 研究）：{string.Join(" ", tabs.Select(t => GameText.Get(MechanicCodex.TabTitleKey(t)) + MechanicCodex.CountIn(t)))}；缺 {string.Join("、", missing)}；空 {string.Join("、", empty)}");

            int mainTags = StatusTagCatalog.Rows.Count(r => r.Kind == "status" && (r.AliasOf == "none" || string.IsNullOrEmpty(r.AliasOf)));
            int regions = WorldGenContent.Territories.Select(t => t.Id).Distinct().Count();
            int components = ComponentCatalog.All.Values.Count(d => d.Category == MechanicalContentCategory.MainComponent || d.Category == MechanicalContentCategory.FunctionComponent
                                                                   || d.Category == MechanicalContentCategory.Structure);
            var counts = new (string Tab, int Got, int Want)[]
            {
                (MechanicCodex.TabEnemy, MechanicCodex.CountIn(MechanicCodex.TabEnemy), EnemyDefs.Rows.Count),
                (MechanicCodex.TabLore, MechanicCodex.CountIn(MechanicCodex.TabLore), NarrativeCatalog.All.Count),
                (MechanicCodex.TabStatus, MechanicCodex.CountIn(MechanicCodex.TabStatus), mainTags),
                (MechanicCodex.TabBuilding, MechanicCodex.CountIn(MechanicCodex.TabBuilding), BuildCatalog.All.Count),
                (MechanicCodex.TabRegion, MechanicCodex.CountIn(MechanicCodex.TabRegion), regions),
                (MechanicCodex.TabComponent, MechanicCodex.CountIn(MechanicCodex.TabComponent), components),
                (MechanicCodex.TabChassis, MechanicCodex.CountIn(MechanicCodex.TabChassis), ChassisCatalog.All.Count),
            };
            var badCount = counts.Where(c => c.Got != c.Want).Select(c => $"{c.Tab} {c.Got}≠{c.Want}").ToList();
            Expect(badCount.Count == 0 && MechanicCodex.CountIn(MechanicCodex.TabFaction) >= 4,
                $"A2 条目由表生成、条数与来源一致（敌人 {EnemyDefs.Rows.Count} / 资料库 {NarrativeCatalog.All.Count} / 状态标签 {mainTags} / 建筑 {BuildCatalog.All.Count} / 区域 {regions} / 组件 {components} / 底盘 {ChassisCatalog.All.Count}；阵营 {MechanicCodex.CountIn(MechanicCodex.TabFaction)}）{string.Join("；", badCount)}");

            bool eventTab = MechanicCodex.TabOf("codex.env.weather") == MechanicCodex.TabEvent && MechanicCodex.TabOf("codex.env.silent_night") == MechanicCodex.TabEvent
                            && MechanicCodex.TabOf("codex.raid.director") == MechanicCodex.TabEvent
                            && MechanicCodex.Entries.Any(e => e.Kind == MechanicCodexKind.Weather) && MechanicCodex.Entries.Any(e => e.Kind == MechanicCodexKind.Hazard);
            Expect(eventTab, "A3 事件页签：天气（fg.TbWeather）+ 区域危害（fg.TbHazard）+ 天气 / 静默夜 / 昼夜 / 危害 / 突袭 / 攻城的事件类系统说明");

            // 全部新条目：中英文标题 / 正文 / 获取提示都有内容，不漏文本键、不留 {0}。
            NarrativeEntryDef anyLore = NarrativeCatalog.All.First(d => !d.IsCutscene);
            NarrativeService.Deliver(s, anyLore);
            var bad = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (MechanicCodexEntry e in MechanicCodex.Entries.Where(x => x.Kind >= MechanicCodexKind.Component))
                {
                    string title = MechanicCodex.Title(e);
                    string hint = MechanicCodex.Hint(e, s);
                    string body = e.Kind == MechanicCodexKind.Lore && e.ContentId != anyLore.Id ? "(未收到)" : MechanicCodex.Body(e, s);
                    foreach ((string what, string text) in new[] { ("标题", title), ("正文", body), ("提示", hint) })
                    {
                        if (string.IsNullOrWhiteSpace(text) || text.Contains("{0}") || text.Contains("codex.") || text.Contains("enemy.role.") || text.Contains("faction."))
                        {
                            bad.Add($"[{lang}] {e.Id} {what}“{Short(text)}”");
                        }
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(bad.Count == 0, $"A4 新页签全部条目中英文标题 / 正文 / 获取提示齐全、无漏键与占位（{MechanicCodex.Entries.Count(x => x.Kind >= MechanicCodexKind.Component)} 条 × 2 种语言）{(bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad.Take(8)))}");

            var dangling = new List<string>();
            foreach (MechanicCodexEntry e in MechanicCodex.Entries)
            {
                foreach (string l in e.Links)
                {
                    if (MechanicCodex.Find(l) == null || l == e.Id)
                    {
                        dangling.Add(e.Id + "→" + l);
                    }
                }
            }
            var enemyLinks = EnemyDefs.Rows.SelectMany(r => (r.CodexLinks ?? "none").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0 && x != "none")
                .Where(x => MechanicCodex.Find(x) == null).Select(x => r.Id + "→" + x)).ToList();
            Expect(dangling.Count == 0 && enemyLinks.Count == 0,
                $"A5 互链全部可达（{MechanicCodex.Entries.Sum(e => e.Links.Length)} 条链接；敌人 codexLinks 列 {EnemyDefs.Rows.Count(r => r.CodexLinks != "none")} 行）{string.Join("；", dangling.Concat(enemyLinks).Take(8))}");

            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "MechanicCodexPanel.uxml");
            VisualElement tree = vta.CloneTree();
            var noButton = tabs.Where(t => tree.Q<Button>(MechanicCodexPanelUIToolkit.TabButtonName(t)) == null).ToList();
            Expect(noButton.Count == 0, $"A6 UXML 每个页签都有按钮（CodexTab + 页签 ID）{string.Join("、", noButton)}");
            string footer = GameText.Get("codex.panel.footer");
            Expect(footer.Length > 0 && !GameText.ContainsMarker(footer) && !footer.Contains("后续") && !footer.Contains("敌人"),
                $"A7 面板脚注只写悬停跳转 / 关闭的按键（“{footer}”），不再写“敌人、阵营等页签将在后续版本加入”（本卡已交付 16 个页签）");
        }

        // ─────────────────────────────── B 首次遭遇（真内核） ───────────────────────────────

        private sealed class BenchRules : CombatSiteRules
        {
        }

        private static CombatSite OpenBench(CampaignState s)
        {
            CombatSites.Close(Bench, s, dropRecord: true);
            return CombatSites.Open(Bench, s, false, new BenchRules(), out _);
        }

        private static int BenchEnemy(CombatSite site, CampaignState s, string typeId, string id, Vector2 at)
        {
            MechEnemy row = EnemyDefs.Get(typeId);
            var rec = new RegionEnemyRecord
            {
                EnemyInstanceId = id, RegionId = Bench, EnemyTypeId = typeId, Position = at, Health = row.MaxHp, MaxHealth = row.MaxHp, IsAlive = true,
            };
            s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(e => e.EnemyInstanceId != id).Append(rec).ToArray();
            if (!EnemySpawnBuilder.TryBuild(site, s, rec, RegionEnemySites.For(Bench), out CombatSpawn sp))
            {
                Fail("夹具：生成 " + typeId + " 失败");
                return 0;
            }
            return site.SpawnEnemy(rec, sp);
        }

        private static int Gunner(CombatSite site, Vector2 at)
        {
            int w = site.WeaponIndex(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 20f, Damage = 5f, Cooldown = 0.1f, TargetMode = CombatTargetMode.Nearest,
            });
            return site.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = new double2(at.x, at.y), Home = new double2(at.x, at.y),
                Radius = 0.6f, Health = 5000f, MaxHealth = 5000f, Weapon = w, BehaviorProfile = -1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
        }

        private static void RunFor(CombatSite site, float seconds, float dt = Dt)
        {
            int n = Mathf.RoundToInt(seconds / dt);
            for (int i = 0; i < n; i++)
            {
                _t += dt;
                site.Step(dt, _t);
            }
        }

        private static void CheckEncounter()
        {
            FreshCodex("encounter");
            CampaignState s = NewState(9311);
            CombatSite site = OpenBench(s);
            _t = 0;
            try
            {
                const string near = "enemy_repairbot";
                const string far = "enemy_strider";
                int nearUnit = BenchEnemy(site, s, near, "b_near", new Vector2(6f, 0f));
                int farUnit = BenchEnemy(site, s, far, "b_far", new Vector2(400f, 0f));
                int gunner = Gunner(site, new Vector2(0f, 0f));
                RunFor(site, 1f); // 不开火：彼此只是在场，不算遭遇
                bool lockedBefore = !MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(near)) && !MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId("foundry"));
                int notify0 = MechanicCodex.NotifyCount;
                // 玩家的攻击命令走内核开火（同 UnitAttack 的正式路径）：命中 → 内核 Damaged 事件。
                CombatFireResult fired = site.Kernel.FireAt(gunner, nearUnit, _t);
                site.ProcessEvents();
                RunFor(site, 0.5f);
                bool nearOpen = MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(near)) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId("foundry"));
                bool farLocked = !MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(far));
                int notify1 = MechanicCodex.NotifyCount;
                bool posted = NotificationCenter.History.Any(n => n.Type?.Id == MechanicCodex.NotifyType && n.Members.Any(m => m.DetailText.Contains(EnemyDefs.NameOf(near))));
                string file = File.Exists(MechanicCodex.FilePath) ? File.ReadAllText(MechanicCodex.FilePath) : string.Empty;
                Expect(lockedBefore && fired == CombatFireResult.Ok && nearOpen && farLocked && notify1 - notify0 == 2 && posted && file.Contains(MechanicCodex.EnemyEntryId(near)),
                    $"B1 首次遭遇（FGT-FAC-008，真内核交火）：在场 1 秒不开火时仍是剪影（{lockedBefore}）；开火（{fired}）命中 {EnemyDefs.NameOf(near)} → 敌人与“{GameText.Get("faction.foundry")}”阵营解锁（{nearOpen}）、" +
                    $"发“图鉴新增”通知 {notify1 - notify0} 条（{posted}）、写进图鉴文件；400 米外没交火的 {EnemyDefs.NameOf(far)} 仍是剪影（{farLocked}）" +
                    (posted ? string.Empty : "；通知历史：" + string.Join(" / ", NotificationCenter.History.Select(n => (n.Type?.Id ?? "?") + ":" + string.Join(",", n.Members.Select(m => m.DetailText))))));
                int enc = MechanicCodex.EncounterCount;
                site.Kernel.FireAt(gunner, nearUnit, _t + 1.0);
                site.ProcessEvents();
                RunFor(site, 2f);
                Expect(MechanicCodex.NotifyCount == notify1 && MechanicCodex.EncounterCount == enc,
                    $"B2 同一局反复交火不重复解锁 / 通知（遭遇计数 {MechanicCodex.EncounterCount}，通知 {MechanicCodex.NotifyCount - notify0}）");
                // 直接受伤（其它系统对具名敌人造成的伤害）与击毁同样算遭遇。
                site.Kernel.Damage(farUnit, 1e6f, 0);
                RunFor(site, 0.2f);
                RegionEnemyRecord farRec = s.RegionEnemies.First(e => e.EnemyInstanceId == "b_far");
                Expect(MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(far)) && !farRec.IsAlive,
                    $"B3 被击毁（内核 Killed 事件）同样算遭遇：{EnemyDefs.NameOf(far)} 解锁，记录阵亡 {!farRec.IsAlive}");
                // 负向：查不到的敌人类型、空 ID 不崩、不解锁任何东西。
                int before = MechanicCodex.UnlockedCount;
                MechanicCodex.OnEnemyEncountered(null);
                MechanicCodex.OnEnemyEncountered("enemy_does_not_exist");
                Expect(MechanicCodex.UnlockedCount == before, "B4 负向：空 / 不存在的敌人类型不解锁、不抛异常");
                // 敌人悬停读数：未遭遇不写弱点（不剧透），遭遇后写；按图鉴键跳到敌人条目。
                MechEnemy hidden = EnemyDefs.Get("enemy_listener");
                TooltipContent lockedTip = StatusTagHover.EnemyContent(hidden.Id, null);
                MechEnemy seen = EnemyDefs.Get(near);
                TooltipContent openTip = StatusTagHover.EnemyContent(near, null);
                Expect(lockedTip.CodexEntryId == MechanicCodex.EnemyEntryId(hidden.Id) && !lockedTip.Body.Contains(GameText.Get(hidden.WeaknessKey))
                       && openTip.Body.Contains(GameText.Get(seen.WeaknessKey)) && openTip.Title == EnemyDefs.NameOf(near),
                    $"B5 敌人悬停读数：标题 = 名字，职能一行；未遭遇的不写弱点（“{Short(lockedTip.Body)}”），遭遇后写弱点（“{Short(openTip.Body)}”）；图鉴键跳到敌人条目");
            }
            finally
            {
                CombatSites.Close(Bench, s, dropRecord: true);
            }
        }

        /// <summary>
        /// B6～B7（复审修复 P1）：另外两条生产触发路径——首领级敌人（ExternalHealth，内核只发伤害请求、不发 Damaged）被玩家打中；
        /// 敌人打中己方机器（伤害请求的机器分支）。都走真实内核开火与 CombatSite.Handle。
        /// </summary>
        private static void CheckEncounterPaths()
        {
            FreshCodex("paths");
            CampaignState s = NewState(9315);
            CombatSite site = OpenBench(s);
            _t = 0;
            try
            {
                // B6 首领级：铸造厂供能节点（structure + external_health）。
                const string boss = "enemy_core_node";
                int node = BenchEnemy(site, s, boss, "b_node", new Vector2(6f, 0f));
                int gunner = Gunner(site, new Vector2(0f, 0f));
                bool external = site.Kernel.TryGetUnit(node, out CombatUnitView nv) && (nv.Flags & CombatUnitFlags.ExternalHealth) != 0;
                bool lockedBefore = !MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(boss));
                int n0 = MechanicCodex.NotifyCount;
                CombatFireResult fired = site.Kernel.FireAt(gunner, node, _t);
                site.ProcessEvents();
                RunFor(site, 0.5f);
                bool bossOpen = MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(boss)) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId("foundry"));
                Expect(external && lockedBefore && fired == CombatFireResult.Ok && bossOpen && MechanicCodex.NotifyCount - n0 == 2,
                    $"B6 首领级敌人（血量在热更层，内核只发伤害请求）被真内核开火打中即首次遭遇：{EnemyDefs.NameOf(boss)}（ExternalHealth {external}，开火 {fired}）解锁敌人 + 阵营（{bossOpen}），通知 {MechanicCodex.NotifyCount - n0} 条");

                // B7 敌人打中己方机器：步行炮台对一台真机器开火（机器不还手、炮台自己没受伤 → 解锁只能来自伤害请求的机器分支）。
                const string shooter = "enemy_strider";
                int strider = BenchEnemy(site, s, shooter, "b_strider", new Vector2(0f, 100f));
                var at = new Vector2(0f, 108f);
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Bench, at, 120f, 120f);
                MachineRecord mrec = r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord m) ? m : null;
                if (mrec != null)
                {
                    MachineLoadoutRegistry.Register(s, mrec.LogicId, mrec.BlueprintId, mrec.BlueprintVersion);
                }
                HomeValleyMachineMarker marker = mrec != null ? site.SpawnMachine(s, mrec, at, autoEngage: false) : null;
                int machineUnit = marker != null && site.TryGetMachineUnit(mrec.LogicId, out int mu) ? mu : 0;
                RegionEnemyRecord srec = s.RegionEnemies.First(e => e.EnemyInstanceId == "b_strider");
                float shooterHp = srec.Health;
                float hp0 = mrec?.Health ?? 0f;
                bool shooterLocked = !MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(shooter));
                CombatFireResult shot = machineUnit != 0 ? site.Kernel.FireAt(strider, machineUnit, _t) : CombatFireResult.TargetMissing;
                site.ProcessEvents();
                for (int i = 0; i < 8 * 60 && (mrec == null || mrec.Health >= hp0); i++)
                {
                    RunFor(site, Dt);
                }
                bool hit = mrec != null && mrec.Health < hp0;
                bool shooterOpen = MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(shooter));
                Expect(machineUnit != 0 && shooterLocked && hit && shooterOpen && Math.Abs(srec.Health - shooterHp) < 1e-3f,
                    $"B7 敌人打中己方机器即首次遭遇（伤害请求的机器分支）：{EnemyDefs.NameOf(shooter)} 开火（{shot}）打中 {mrec?.ChassisId}（耐久 {hp0:0} → {mrec?.Health:0}）→ 解锁（{shooterOpen}）；炮台自己没受伤（{srec.Health:0}/{shooterHp:0}）");

                // B6b（复审 P2）首领级被直接击毁（Killed 分支，没有先打中、没有伤害请求）：图鉴从空白起步，解锁只能来自 Killed 分支挪到 ExternalHealth 提前返回之前的那一行。
                FreshCodex("paths_kill");
                int node2 = BenchEnemy(site, s, boss, "b_node2", new Vector2(-60f, -60f));
                bool locked2 = !MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(boss)) && !MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId("foundry"));
                int n2 = MechanicCodex.NotifyCount;
                bool killed = site.Kernel.Kill(node2, gunner);
                site.ProcessEvents();
                bool killOpen = MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(boss)) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId("foundry"));
                Expect(locked2 && killed && killOpen && MechanicCodex.NotifyCount - n2 == 2,
                    $"B6b 首领级敌人被直接击毁（只有 Killed 事件、没有伤害请求）也算首次遭遇：空白图鉴（{locked2}）→ 击毁（{killed}）→ 敌人 + 阵营解锁（{killOpen}），通知 {MechanicCodex.NotifyCount - n2} 条");
            }
            finally
            {
                CombatSites.Close(Bench, s, dropRecord: true);
            }
        }

        /// <summary>
        /// B8～B9（复审修复 P1 / P2）：悬停拾取走生产函数 StatusTagHover.PickAt（Tick 每帧调的同一个）：没有标签的敌人也弹敌人读数并首次遭遇解锁；
        /// 负向：静默潜伏（Stealthed）与不可选中（藏身 / 休眠阶段的首领本体没有 Targetable）的敌人不弹、不解锁、不发通知。暂停中同样可以悬停查看。
        /// </summary>
        private static void CheckHoverPick()
        {
            FreshCodex("hover");
            CampaignState s = NewState(9317);
            CombatSite site = OpenBench(s);
            _t = 0;
            try
            {
                const string type = "enemy_scout";
                int scout = BenchEnemy(site, s, type, "h_scout", new Vector2(60f, 0f));
                site.Kernel.TryGetUnit(scout, out CombatUnitView v);
                double2 at = v.Position + new double2(0.3, 0.0);
                string id = MechanicCodex.EnemyEntryId(type);
                List<(string Glyph, string Name, float Seconds, int Stacks)> tags = StatusTagHover.Describe(site.Kernel, scout);
                bool untagged = tags == null || tags.Count == 0;
                int n0 = MechanicCodex.NotifyCount;
                site.SetUnitFlag(scout, CombatUnitFlags.Stealthed, true);
                int stealthPick = StatusTagHover.HoverGroundForTests(site, at);
                bool stealthQuiet = stealthPick == 0 && !MechanicCodex.IsUnlocked(id) && MechanicCodex.NotifyCount == n0 && StatusTagHover.HoveredUnit == 0;
                site.SetUnitFlag(scout, CombatUnitFlags.Stealthed, false);
                site.SetUnitFlag(scout, CombatUnitFlags.Targetable, false);
                int hiddenPick = StatusTagHover.HoverGroundForTests(site, at);
                bool hiddenQuiet = hiddenPick == 0 && !MechanicCodex.IsUnlocked(id) && MechanicCodex.NotifyCount == n0;
                Expect(untagged && stealthQuiet && hiddenQuiet,
                    $"B8 负向：光标扫过潜伏中（Stealthed）/ 不可选中（藏身的首领本体同样没有 Targetable）的 {EnemyDefs.NameOf(type)} 不弹悬停读数（拾取 {stealthPick} / {hiddenPick}）、不解锁、不发通知（{stealthQuiet} / {hiddenQuiet}）");
                site.SetUnitFlag(scout, CombatUnitFlags.Targetable, true);
                GameClock.SetPaused(true);
                int pick = StatusTagHover.HoverGroundForTests(site, at);
                TooltipContent tip = StatusTagHover.HoveredContentForTests();
                bool open = MechanicCodex.IsUnlocked(id) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId("silent"));
                int notes = MechanicCodex.NotifyCount - n0;
                StatusTagHover.HoverGroundForTests(site, at); // 停着不动：不重复解锁 / 通知
                bool once = MechanicCodex.NotifyCount - n0 == notes;
                GameClock.SetPaused(false);
                Expect(pick == scout && tip != null && tip.Title == EnemyDefs.NameOf(type) && tip.CodexEntryId == id && open && notes == 2 && once,
                    $"B9 没有标签的敌人：光标停上去（暂停中同样可以看）弹出敌人悬停读数“{tip?.Title}”、图鉴键跳 {tip?.CodexEntryId}；看到即首次遭遇，解锁敌人 + 阵营、通知 {notes} 条，停着不动不重复（{once}）");
            }
            finally
            {
                GameClock.SetPaused(false);
                StatusTagHover.Leave(null);
                UiTooltip.ResetForTests();
                CombatSites.Close(Bench, s, dropRecord: true);
            }
        }

        // ─────────────────────────────── T 暂停与倍速 ───────────────────────────────

        private static void CheckTiming()
        {
            var results = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0f, 0.5f, 1f, 2f, 3f })
            {
                FreshCodex("speed" + speed);
                CampaignState s = NewState(9320 + (int)(speed * 10));
                CombatSite site = OpenBench(s);
                _t = 0;
                try
                {
                    int scout = BenchEnemy(site, s, "enemy_scout", "t_scout", new Vector2(6f, 0f));
                    int gunner = Gunner(site, new Vector2(0f, 0f));
                    int n0 = MechanicCodex.NotifyCount;
                    if (speed > 0f)
                    {
                        // 倍速 = 每帧的游戏时间步变大（同一段真实时长）：开火后按该倍速推进 1 秒真实时间。
                        site.Kernel.FireAt(gunner, scout, _t);
                        site.ProcessEvents();
                        RunFor(site, 1f * speed, Dt * speed);
                    }
                    else
                    {
                        // 暂停：世界不步进（GameClock 暂停时地点不 Step），排队的开火不结算。
                        site.Kernel.FireAt(gunner, scout, _t);
                    }
                    // speed 0 = 暂停：世界不步进，内核不产生事件。
                    bool open = MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId("enemy_scout"));
                    int notes = MechanicCodex.NotifyCount - n0;
                    bool good = speed > 0f ? open && notes == 2 : !open && notes == 0;
                    ok &= good;
                    results.Add($"{(speed > 0f ? speed + "x" : "暂停")}：{(open ? "解锁" : "未解锁")} / 通知 {notes}");
                }
                finally
                {
                    CombatSites.Close(Bench, s, dropRecord: true);
                }
            }
            Expect(ok, "T1 暂停时（地点不步进）内核不产生遭遇事件，交火不解锁（悬停查看照常解锁，见 B9）；0.5x / 1x / 2x / 3x 下首次遭遇都恰好解锁一次、通知一次：" + string.Join("；", results));
        }

        // ─────────────────────────────── C 跨存档 / 按存档 ───────────────────────────────

        private static void CheckPersistence()
        {
            string file = FreshCodex("persist");
            CampaignState a = NewState(9331, Slot);
            NarrativeEntryDef lore = NarrativeCatalog.All.First(d => d.Source == NarrativeCatalog.SourceTerminal);
            string loreId = MechanicCodex.LoreEntryId(lore.Id);
            string loreBody = GameText.Get(lore.BodyKey);
            string probe = loreBody.Length > 12 ? loreBody.Substring(2, 8) : loreBody;
            bool lockedFirst = !MechanicCodex.IsUnlocked(loreId);
            NarrativeService.Deliver(a, lore);
            bool openA = MechanicCodex.IsUnlocked(loreId) && MechanicCodex.Body(MechanicCodex.Find(loreId), a).Contains(loreBody);
            MechanicCodex.OnEnemyEncountered("enemy_jammer");
            bool loreUnlockRefused = !MechanicCodex.Unlock(loreId);
            string text = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
            Expect(lockedFirst && openA && loreUnlockRefused && text.Contains("enemy:enemy_jammer") && !text.Contains(MechanicCodex.LorePrefix),
                $"C1 存档 A：收到资料“{NarrativeService.TitleOf(lore)}”后资料库页签显示正文；机制条目（敌人）写进图鉴文件，图鉴文件里没有任何资料库条目（Unlock(lore) 被拒绝）");

            // 重启游戏（内存清空，只剩图鉴文件）：机制条目还在。
            MechanicCodex.Reload();
            bool afterRestart = MechanicCodex.IsUnlocked("enemy:enemy_jammer");

            // 新存档 B：机制条目仍在（跨存档）；资料库是剪影 + 获取提示，正文、标题都看不到，搜索也搜不到 A 的剧情。
            CampaignState b = NewState(9332, SlotB);
            MechanicCodexEntry e = MechanicCodex.Find(loreId);
            var hits = new List<MechanicCodexEntry>();
            MechanicCodex.EntriesIn(MechanicCodex.TabLore, probe, hits, b);
            string hint = MechanicCodex.Hint(e, b);
            Expect(afterRestart && MechanicCodex.IsUnlocked("enemy:enemy_jammer") && !MechanicCodex.IsUnlocked(loreId) && !hint.Contains(probe) && hits.Count == 0
                   && MechanicCodex.UnlockedCountIn(MechanicCodex.TabLore) == 0,
                $"C2 负向“新存档里看不到其他存档的剧情文本”（FGT-UX-006）：重启后机制条目仍在；新存档 B 里敌人条目仍解锁，资料库 0/{MechanicCodex.CountIn(MechanicCodex.TabLore)}，" +
                $"该条是剪影 + 获取提示（“{Short(hint)}”），搜“{probe}”0 条");

            // 切回 A：A 的资料库条目回来（同一份图鉴文件，资料库看存档）。
            CampaignSession.Set(Slot, a);
            Expect(MechanicCodex.IsUnlocked(loreId) && MechanicCodex.UnlockedCountIn(MechanicCodex.TabLore) == NarrativeService.Count(a),
                $"C3 切回存档 A：资料库 {MechanicCodex.UnlockedCountIn(MechanicCodex.TabLore)} 条 = A 收到的 {NarrativeService.Count(a)} 条");

            // 真文件存读档：资料库条目随存档读回；机制条目不依赖存档。
            SaveResult save = CampaignSaveService.Save(Slot, a, SaveReason.Manual);
            LoadResult load = CampaignSaveService.Load(Slot);
            CampaignState l = load.State;
            if (l != null)
            {
                CampaignSession.Set(Slot, l);
            }
            Expect(save.Success && l != null && MechanicCodex.IsUnlocked(loreId) && NarrativeService.Has(l, lore.Id) && MechanicCodex.IsUnlocked("enemy:enemy_jammer"),
                $"C4 真文件存读档：读回的存档里资料库条目仍解锁（{save.Success} / {load.Outcome}），机制条目不受影响");

            // 图鉴文件损坏：按空图鉴继续，不抛异常；资料库照旧看存档。
            File.WriteAllText(file, "{not json");
            MechanicCodex.Reload();
            Expect(!MechanicCodex.IsUnlocked("enemy:enemy_jammer") && MechanicCodex.IsUnlocked(loreId),
                "C5 负向：图鉴文件损坏 → 机制条目按空图鉴继续（不崩），资料库仍按存档显示");
        }

        // ─────────────────────────────── D 搜索与互链 ───────────────────────────────

        private static void CheckSearchAndLinks()
        {
            FreshCodex("search");
            CampaignState s = NewState(9341);
            string enemy = "enemy_armorbot";
            string name = EnemyDefs.NameOf(enemy);
            var hits = new List<MechanicCodexEntry>();
            MechanicCodex.EntriesIn(MechanicCodex.TabEnemy, name, hits, s);
            bool lockedHidden = hits.Count == 0;
            MechanicCodex.EntriesIn(MechanicCodex.TabEnemy, "遭遇", hits, s);
            int byHint = hits.Count;
            MechanicCodex.OnEnemyEncountered(enemy);
            MechanicCodex.EntriesIn(MechanicCodex.TabEnemy, name, hits, s);
            bool openFound = hits.Any(h => h.ContentId == enemy);
            MechanicCodex.EntriesIn(MechanicCodex.TabEnemy, GameText.Get(EnemyDefs.Get(enemy).WeaknessKey).Substring(0, 4), hits, s);
            bool byBody = hits.Any(h => h.ContentId == enemy);
            Expect(lockedHidden && byHint == EnemyDefs.Rows.Count && openFound && byBody,
                $"D1 搜索：没遭遇的敌人搜名字搜不到（不剧透），只按获取提示能搜到（{byHint} 条）；遭遇后按名字 / 正文（弱点）都能搜到");

            bool Linked(string x, string y) => MechanicCodex.Find(x) != null && MechanicCodex.Find(x).Links.Contains(y) && MechanicCodex.Find(y).Links.Contains(x);
            string fireTag = MechanicCodex.StatusEntryId("Fire");
            MechanicCodexEntry fire = MechanicCodex.Find(fireTag);
            string fireReaction = fire?.Links.FirstOrDefault(l => l.StartsWith(MechanicCodex.ReactionPrefix, StringComparison.Ordinal));
            string fireFirmware = fire?.Links.FirstOrDefault(l => l.StartsWith(MechanicCodex.FirmwarePrefix, StringComparison.Ordinal));
            BuildEntry researched = BuildCatalog.All.FirstOrDefault(b => ResearchGate.IsResearchRule(b.UnlockRule));
            string bId = researched != null ? MechanicCodex.BuildingEntryId(researched.Id) : null;
            string nodeId = researched != null ? MechanicCodex.ResearchEntryId(ResearchGate.NodeOf(researched.UnlockRule)) : null;
            Hazard hz = HomeExplorationService.Hazards.First();
            var pairs = new (string A, string B, string What)[]
            {
                (fireTag, fireReaction, "状态标签 ↔ 反应"),
                (fireTag, fireFirmware, "状态标签 ↔ 固件"),
                (MechanicCodex.EnemyEntryId(enemy), "reaction:reaction_meltoverload", "敌人弱点 ↔ 反应"),
                (MechanicCodex.EnemyEntryId("enemy_echo_hunter"), "comp:func_marker", "敌人弱点 ↔ 组件"),
                (MechanicCodex.EnemyEntryId("enemy_scout"), MechanicCodex.FactionEntryId("silent"), "敌人 ↔ 阵营"),
                (MechanicCodex.FactionEntryId("silent"), MechanicCodex.RegionEntryId("silent"), "阵营 ↔ 区域"),
                (MechanicCodex.RegionEntryId(hz.TerritoryId), MechanicCodex.HazardEntryId(hz.Id), "区域 ↔ 危害"),
                (bId, nodeId, "建筑 ↔ 研究"),
            };
            var broken = pairs.Where(p => p.A == null || p.B == null || !Linked(p.A, p.B)).Select(p => $"{p.What}（{p.A}/{p.B}）").ToList();
            Expect(broken.Count == 0, $"D2 互链双向（FGR-UX-051“固件 ↔ 反应 ↔ 敌人弱点”）：{string.Join("、", pairs.Select(p => p.What))}{(broken.Count == 0 ? string.Empty : "；断开：" + string.Join("；", broken))}");
        }

        // ─────────────────────────────── E 悬停一键跳转 ───────────────────────────────

        private static void CheckHoverJump()
        {
            FreshCodex("hover");
            CampaignState s = NewState(9351);
            BuildEntry building = BuildCatalog.All.First(b => !b.IsTool);
            ItemDef item = ItemCatalog.Items.First();
            NarrativeEntryDef lore = NarrativeCatalog.All.First();
            string explicitId = MechanicCodex.FirmwareEntryId(FirmwareKinds.Rows.First().Id);
            bool resolve = UiTooltip.CodexIdOf(new TooltipContent { Title = building.Name }) == MechanicCodex.BuildingEntryId(building.Id)
                           && UiTooltip.CodexIdOf(new TooltipContent { Title = GameText.Get(item.NameKey) }) == MechanicCodex.ItemEntryId(item.Id)
                           && UiTooltip.CodexIdOf(new TooltipContent { Title = EnemyDefs.NameOf("enemy_scout") }) == MechanicCodex.EnemyEntryId("enemy_scout")
                           && UiTooltip.CodexIdOf(new TooltipContent { Title = building.Name, CodexEntryId = explicitId }) == explicitId
                           && UiTooltip.CodexIdOf(new TooltipContent { Title = NarrativeService.TitleOf(lore) }) == null
                           && UiTooltip.CodexIdOf(new TooltipContent { Title = "并不存在的名字" }) == null
                           && UiTooltip.CodexIdOf(new TooltipContent { Title = building.Name, CodexEntry = "只给文字" }) == null;
            Expect(resolve, $"E1 任意悬停提示的图鉴条目：显式条目优先；没写时按标题找同名条目（建筑“{building.Name}”、物品“{GameText.Get(item.NameKey)}”、敌人）；剧情标题与无关标题不当链接");

            VisualElement root = MountUxml(UiKitFolder + "MechanicCodexPanel.uxml", out GameObject go);
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = true;
            var target = new VisualElement { name = "__codex_hover_target" };
            root.Add(target);
            try
            {
                var panel = go.AddComponent<MechanicCodexPanelUIToolkit>();
                panel.BindView(root);
                InputRouter.Reset();
                InputRouter.SetScope(InputScope.Strategy);
                InputRouter.DebugSetReader(Keys);
                UiTooltip.Attach(target, () => new TooltipContent { Title = building.Name, Body = "（悬停测试）" });
                UiTooltip.NotifyEnter(target);
                int jumps = CodexHoverLink.JumpCount;
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                bool jumped = CodexHoverLink.JumpCount == jumps + 1 && MechanicCodexPanelUIToolkit.IsOpen && panel.SelectedId == MechanicCodex.BuildingEntryId(building.Id)
                              && panel.CurrentTab == MechanicCodex.TabBuilding;
                UiTooltip.NotifyLeave(target);
                UiTooltip.Hide();
                MechanicCodexPanelUIToolkit.Close();
                // 敌人：悬停读数（世界对象）同一套——未遭遇时跳到剪影 + 获取提示，不解锁。
                UiTooltip.Attach(target, () => StatusTagHover.EnemyContent("enemy_mind_core", null));
                UiTooltip.NotifyEnter(target);
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                bool enemyJump = MechanicCodexPanelUIToolkit.IsOpen && panel.SelectedId == MechanicCodex.EnemyEntryId("enemy_mind_core")
                                 && !MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId("enemy_mind_core")) && panel.EntryTitleText == GameText.Get("codex.panel.locked_title");
                UiTooltip.NotifyLeave(target);
                UiTooltip.Hide();
                MechanicCodexPanelUIToolkit.Close();
                Expect(jumped && enemyJump,
                    $"E2 悬停提示时按图鉴键（{InputDisplay.ForAction(GameActionId.OpenCodex)}）一键跳转：建筑提示（只有标题）→ 建筑页签的该条；敌人悬停读数 → 敌人页签剪影（跳转不解锁）");
            }
            finally
            {
                UiTooltip.ResetForTests();
                MechanicCodexPanelUIToolkit.Close();
                InputRouter.DebugSetReader(null);
                Object.DestroyImmediate(go);
            }
        }

        // ─────────────────────────────── F 面板、剪影与获取提示、资料库键 ───────────────────────────────

        private static void CheckPanel()
        {
            FreshCodex("panel");
            CampaignState s = NewState(9361);
            NarrativeEntryDef lore = NarrativeCatalog.All.First(d => d.Source == NarrativeCatalog.SourceTerminal);
            NarrativeService.Deliver(s, lore);
            VisualElement root = MountUxml(UiKitFolder + "MechanicCodexPanel.uxml", out GameObject go);
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<MechanicCodexPanelUIToolkit>();
                panel.BindView(root);
                InputRouter.Reset();
                InputRouter.SetScope(InputScope.Strategy);
                InputRouter.DebugSetReader(Keys);
                panel.OpenAt(null);
                var tabProblems = new List<string>();
                for (int i = 0; i < MechanicCodex.Tabs.Length; i++)
                {
                    string tab = MechanicCodex.Tabs[i];
                    Click(panel.TabButton(i));
                    panel.Refresh();
                    string text = panel.TabText(i);
                    if (panel.CurrentTab != tab || panel.ItemCount != MechanicCodex.CountIn(tab) || !text.StartsWith(GameText.Get(MechanicCodex.TabTitleKey(tab)), StringComparison.Ordinal)
                        || !text.Contains("/" + MechanicCodex.CountIn(tab)))
                    {
                        tabProblems.Add($"{tab}（当前 {panel.CurrentTab}，{panel.ItemCount} 条，“{text}”）");
                    }
                }
                Expect(tabProblems.Count == 0, $"F1 16 个页签按钮逐个点：切到对应页签，列表条数 = 该页条目数，按钮写“页签名 已解锁/总数”{string.Join("；", tabProblems)}");

                // 未解锁：“？？？” + 剪影图标 + 获取提示（不剧透名字 / 弱点）；解锁后显示名字与弱点。
                string enemy = "enemy_strider";
                string id = MechanicCodex.EnemyEntryId(enemy);
                panel.Select(id);
                panel.Refresh();
                MechEnemy row = EnemyDefs.Get(enemy);
                bool silhouette = panel.EntryTitleText == GameText.Get("codex.panel.locked_title") && panel.EntryIconSilhouette && !panel.EntryIconHidden
                                  && !panel.EntryBodyText.Contains(EnemyDefs.NameOf(enemy)) && !panel.EntryBodyText.Contains(GameText.Get(row.WeaknessKey))
                                  && panel.EntryBodyText.Contains("遭遇") && panel.RelatedCount == 0;
                int lockedRows = Enumerable.Range(0, panel.ItemCount).Count(i => panel.ItemText(i) == GameText.Get("codex.panel.locked_title"));
                MechanicCodex.OnEnemyEncountered(enemy);
                panel.Refresh();
                bool opened = panel.EntryTitleText == EnemyDefs.NameOf(enemy) && !panel.EntryIconSilhouette && panel.EntryBodyText.Contains(GameText.Get(row.WeaknessKey))
                              && panel.EntryBodyText.Contains(GameText.Get("faction.foundry")) && panel.RelatedCount > 0;
                Expect(silhouette && lockedRows == EnemyDefs.Rows.Count && opened,
                    $"F2 卡片“未解锁的剪影和获取提示”：未遭遇的敌人列表里 {lockedRows} 条“？？？”，详情是剪影图标 + 获取提示（“{Short(MechanicCodex.Hint(MechanicCodex.Find(id), s))}”），不剧透名字 / 弱点 / 链接；" +
                    "遭遇后显示名字、弱点、阵营与相关条目");

                // 组件条目：载体、数值、炮塔兼容、三套机身状态与来源（FG-GAP-060）。
                string comp = MechanicCodex.ComponentEntryId(ComponentCatalog.CompRamId);
                panel.Select(comp);
                panel.Refresh();
                string compBody = panel.EntryBodyText;
                bool compOk = compBody.Contains(GameText.Get("morph.state.limiter")) && compBody.Contains(GameText.Get("morph.state.fluid")) && compBody.Contains(GameText.Get("morph.state.em"))
                              && compBody.Contains("炮塔") && compBody.Contains("载体");
                Expect(compOk, $"F3 组件条目（FG-GAP-060）：载体、数值、炮塔兼容、三套机身状态（由哪类固件引起、已拿到哪些）：“{Short(compBody)}”");

                // 资料库页签：读过 = 资料库已读（同一份存档记录）；资料库键 Alt+J 开 / 关。上面逐个点页签时已读过第一条，这里另收一条新的。
                MechanicCodexPanelUIToolkit.Close();
                lore = NarrativeCatalog.All.First(d => d.Source == NarrativeCatalog.SourceTerminal && !NarrativeService.Has(s, d.Id));
                NarrativeService.Deliver(s, lore);
                bool unreadBefore = NarrativeService.Find(s, lore.Id)?.Read == false;
                KeyCode loreKey = GameSettings.KeyBindings.GetKey(GameActionId.OpenLore);
                Press(loreKey, alt: true);
                bool loreOpen = MechanicCodexPanelUIToolkit.IsOpen && panel.CurrentTab == MechanicCodex.TabLore;
                panel.Select(MechanicCodex.LoreEntryId(lore.Id));
                panel.Refresh();
                bool readNow = NarrativeService.Find(s, lore.Id)?.Read == true && panel.EntryBodyText.Contains(GameText.Get(lore.BodyKey));
                Press(loreKey, alt: true);
                bool loreClosed = !MechanicCodexPanelUIToolkit.IsOpen;
                Press(loreKey, alt: false);
                bool plainJNoLore = !MechanicCodexPanelUIToolkit.IsOpen;
                Line($"      （资料库键 {loreKey}，未读 {unreadBefore}，打开到资料库 {loreOpen}，读过 {readNow}，再按关闭 {loreClosed}，单按 J 不开 {plainJNoLore}）");
                Expect(loreKey == KeyCode.J && unreadBefore && loreOpen && readNow && loreClosed && plainJNoLore,
                    $"F4 资料库键（DEBT-FG9NAR01-04，默认 {InputDisplay.ForAction(GameActionId.OpenLore)}）：打开到资料库页签，读过的资料在资料库里也算已读，停在资料库页签再按一次关闭；单按 J 不触发");

                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "MechanicCodexPanel.uxml", "CodexWindow", stressFill: true, prepare: r =>
                {
                    var probeGo = new GameObject("__probe_codex_fg9") { hideFlags = HideFlags.HideAndDontSave };
                    MechanicCodexPanelUIToolkit p = probeGo.AddComponent<MechanicCodexPanelUIToolkit>();
                    p.BindView(r.panel.visualTree);
                    p.OpenAt(MechanicCodex.EnemyEntryId(enemy));
                    p.SetOpen(false);
                    Object.DestroyImmediate(probeGo);
                    r.panel.visualTree.Q<VisualElement>("CodexRoot")?.RemoveFromClassList("uk-hidden");
                });
                panel.BindView(root); // 探针里的临时面板换掉了静态实例：绑回来
                Expect(probe.StartsWith("PASS", StringComparison.Ordinal), "F5 图鉴面板布局探针（16 个页签换行、四种分辨率、超长文字）：" + (probe.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : probe.Replace("\n", " | ").Substring(0, Math.Min(600, probe.Length))));
            }
            finally
            {
                MechanicCodexPanelUIToolkit.Close();
                InputRouter.DebugSetReader(null);
                Object.DestroyImmediate(go);
            }
        }

        // ─────────────────────────────── G 补解锁 ───────────────────────────────

        private static void CheckSync()
        {
            FreshCodex("sync");
            CampaignState s = NewState(9371);
            // 本存档已击毁的敌人（旧档 / 换机后）、拿到过的固件能施加的状态标签、已揭开的危害。
            s.RegionEnemies = new[] { new RegionEnemyRecord { EnemyInstanceId = "dead1", RegionId = "x", EnemyTypeId = "enemy_prism", IsAlive = false } };
            GameConfig.fg.FirmwareKind fw = FirmwareKinds.Rows.First(r => !string.IsNullOrEmpty(r.Tags) && r.Tags != "none"
                                                                        && r.Tags.Split(',').Any(t => StatusTagCatalog.IsStatus(t.Trim())));
            string tag = fw.Tags.Split(',').Select(t => t.Trim()).First(t => StatusTagCatalog.IsStatus(t));
            PrimitiveInventory.GrantCrafted(s, fw.Id);
            Hazard hz = HomeExplorationService.Hazards.First();
            HomeExplorationService.StateOf(s).HazardsSeen = new[] { hz.Id };
            bool before = !MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId("enemy_prism")) && !MechanicCodex.IsUnlocked(MechanicCodex.StatusEntryId(tag))
                          && !MechanicCodex.IsUnlocked(MechanicCodex.HazardEntryId(hz.Id));
            int n = FirmwareLibrary.OnCampaignEntered(s);
            bool after = MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId("enemy_prism")) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId("silent"))
                         && MechanicCodex.IsUnlocked(MechanicCodex.StatusEntryId(tag)) && MechanicCodex.IsUnlocked(MechanicCodex.HazardEntryId(hz.Id));
            BuildEntry always = BuildCatalog.All.First(b => b.UnlockRule == "always");
            BuildEntry gated = BuildCatalog.All.FirstOrDefault(b => b.UnlockRule != "always" && !BuildCatalog.IsUnlocked(s, b));
            bool buildings = MechanicCodex.IsUnlocked(MechanicCodex.BuildingEntryId(always.Id))
                             && (gated == null || !MechanicCodex.IsUnlocked(MechanicCodex.BuildingEntryId(gated.Id)));
            int n2 = FirmwareLibrary.SyncCodex(s);
            Expect(before && after && n > 0 && n2 == 0 && buildings,
                $"G1 进入战役补解锁（旧档 / 换机）：已击毁的敌人 + 阵营、拿到的固件“{FirmwareKinds.DisplayName(fw.Id)}”能施加的“{StatusTagCatalog.NameOf(tag)}”、已揭开的危害；开局可建的建筑直接可看、未解锁的建筑仍是剪影（{gated?.Name ?? "无"}）；再同步一次不重复（{n} → {n2}）");
            // 状态标签：在单位身上看到即解锁（悬停读数入口）。
            string other = StatusTagCatalog.Rows.First(r => r.Kind == "status" && (r.AliasOf == "none" || string.IsNullOrEmpty(r.AliasOf))
                                                             && !MechanicCodex.IsUnlocked(MechanicCodex.StatusPrefix + r.Id)).Id;
            MechanicCodex.OnStatusSeen(other);
            MechanicCodex.OnStatusSeen("ExplodeOnHit"); // 机制标记不是状态标签：不解锁
            Expect(MechanicCodex.IsUnlocked(MechanicCodex.StatusPrefix + other) && MechanicCodex.Find(MechanicCodex.StatusPrefix + "ExplodeOnHit") == null,
                $"G2 在单位身上看到“{StatusTagCatalog.NameOf(other)}”即解锁；机制标记（命中爆开）不进状态标签页");
            // 区域 / 天气：进入领地与天气开始的入口（远征行军与镜头焦点、天气前线都调这里）。
            MechanicCodex.OnTerritoryEntered("clarity");
            MechanicCodex.OnWeatherStarted(WeatherRowsFirst());
            Expect(MechanicCodex.IsUnlocked(MechanicCodex.RegionEntryId("clarity")) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId("clarity"))
                   && MechanicCodex.IsUnlocked(MechanicCodex.WeatherEntryId(WeatherRowsFirst())),
                "G3 第一次进入领地解锁区域 + 领地阵营（还没有敌人入表的阵营也能从领地解锁）；第一次经历天气解锁事件条目");
        }

        /// <summary>
        /// G4～G6（复审修复 P1）：进入领地、天气开始、揭开危害三处解锁走真实生产调用点，不直调解锁接口：
        /// 远征行军的领地判定（ExpeditionMarchService.EvaluateEncounters，世界模拟步里调）与镜头焦点 HUD（WorldPlacesHudUIToolkit.Scan）；
        /// 天气前线按步推进（WeatherService.WorldStep）；揭开新区域（HomeExplorationService.OnAreaRevealed）与开局 / 旧档静默补登（EnsureBaseline，不发通知）。
        /// </summary>
        private static void CheckRealEntryPoints()
        {
            FreshCodex("entry");
            CampaignState s = NewState(9373);
            WorldPlan plan = WorldGenService.PlanFor(s);
            List<PlannedTerritory> factions = plan?.Territories.Where(t => t != null && t.IsFaction).ToList() ?? new List<PlannedTerritory>();
            if (factions.Count < 2)
            {
                Fail($"夹具：世界里阵营领地不足 2 片（{factions.Count}）");
                return;
            }
            // G4 远征行军进入领地（与 FgTerritorySelfCheck I2 同一个生产判定）。
            PlannedTerritory ta = factions[0];
            var march = new ExpeditionMarchState { SiteId = FracturedCityLayout.RegionId, EncounteredIds = Array.Empty<string>() };
            System.Reflection.MethodInfo eval = typeof(ExpeditionMarchService).GetMethod("EvaluateEncounters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            bool aBefore = !MechanicCodex.IsUnlocked(MechanicCodex.RegionEntryId(ta.Id));
            eval.Invoke(null, new object[] { s, march, new Vector2(plan.CoreX, plan.CoreY) });
            eval.Invoke(null, new object[] { s, march, new Vector2(ta.CenterX, ta.CenterY) });
            bool aOpen = MechanicCodex.IsUnlocked(MechanicCodex.RegionEntryId(ta.Id)) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId(ta.Id));
            // G4 续：镜头焦点进入另一片领地（HUD 每帧扫描；第一次看到焦点只记基线、不弹不解锁）。
            PlannedTerritory tb = factions[1];
            var go = new GameObject("FgCodexTerritoryHud");
            bool bBaselineQuiet;
            bool bOpen;
            try
            {
                VisualElement root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "WorldPlacesHud.uxml").CloneTree();
                WorldPlacesHudUIToolkit hud = go.AddComponent<WorldPlacesHudUIToolkit>();
                hud.BindView(root);
                hud.ResetBannerForTests();
                hud.Scan(s, new Vector2(tb.CenterX, tb.CenterY)); // 基线
                bBaselineQuiet = !MechanicCodex.IsUnlocked(MechanicCodex.RegionEntryId(tb.Id));
                hud.Scan(s, new Vector2(plan.CoreX, plan.CoreY));
                hud.Scan(s, new Vector2(tb.CenterX, tb.CenterY));
                bOpen = MechanicCodex.IsUnlocked(MechanicCodex.RegionEntryId(tb.Id)) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId(tb.Id));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            Expect(aBefore && aOpen && bBaselineQuiet && bOpen,
                $"G4 进入领地走真实调用点：远征队伍中心进入“{GameText.Get(ta.NameKey)}”（行军领地判定）→ 区域 + 阵营解锁（{aOpen}）；镜头焦点进入“{GameText.Get(tb.NameKey)}”（领地 HUD 扫描）→ 解锁（{bOpen}），开局第一次看到焦点只记基线不解锁（{bBaselineQuiet}）");

            // G5 天气前线按世界步推进到开始。
            WeatherService.Install();
            try
            {
                string kind = WeatherService.Sandstorm;
                bool wBefore = !MechanicCodex.IsUnlocked(MechanicCodex.WeatherEntryId(kind));
                bool scheduled = WeatherService.ScheduleForTests(s, kind, 1.0, 60.0);
                long t0 = GameClock.Ticks;
                long until = t0 + GameClock.TicksFor(3.0);
                for (long k = t0 + 1; k <= until && !WeatherService.IsActive(s, kind); k++)
                {
                    WeatherService.WorldStep(s, k);
                }
                bool active = WeatherService.IsActive(s, kind);
                bool wOpen = MechanicCodex.IsUnlocked(MechanicCodex.WeatherEntryId(kind));
                Expect(wBefore && scheduled && active && wOpen,
                    $"G5 天气开始走真实调用点：排定的{WeatherService.NameOf(kind)}按世界步推进到开始（{active}）→ 事件页签的天气条目解锁（{wOpen}）");
            }
            finally
            {
                WeatherService.ResetForTests();
            }

            // G6 揭开危害：开局 / 旧档静默补登只解锁不发通知；揭开新区域时第一次发现解锁并发通知。
            var hz = factions.Select(t => (T: t, H: HomeExplorationService.HazardForTerritory(t.Id))).Where(x => x.H != null)
                .GroupBy(x => x.H.Id).Select(g => g.First()).ToList();
            if (hz.Count < 2)
            {
                Fail($"夹具：带危害的阵营领地不足 2 种（{hz.Count}）");
                return;
            }
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            st.Baselined = false;
            st.HazardsSeen = Array.Empty<string>();
            s.Grid.Explored = (s.Grid.Explored ?? Array.Empty<ExploredAreaRecord>())
                .Append(new ExploredAreaRecord { CenterX = hz[0].T.CenterX, CenterY = hz[0].T.CenterY, Radius = 8 }).ToArray();
            int q0 = MechanicCodex.NotifyCount;
            HomeExplorationService.EnsureBaseline(s);
            bool silentOpen = MechanicCodex.IsUnlocked(MechanicCodex.HazardEntryId(hz[0].H.Id));
            bool silentQuiet = MechanicCodex.NotifyCount == q0;
            bool hBefore = !MechanicCodex.IsUnlocked(MechanicCodex.HazardEntryId(hz[1].H.Id));
            HomeExplorationService.OnAreaRevealed(s, hz[1].T.CenterX, hz[1].T.CenterY, 8);
            bool hOpen = MechanicCodex.IsUnlocked(MechanicCodex.HazardEntryId(hz[1].H.Id));
            int hNotes = MechanicCodex.NotifyCount - q0;
            Expect(silentOpen && silentQuiet && hBefore && hOpen && hNotes == 1,
                $"G6 揭开危害走真实调用点：开局 / 旧档补登（已探索圆里的“{hz[0].H.Id}”）静默解锁、不发“图鉴新增”（{silentQuiet}）；揭开新区域第一次发现“{hz[1].H.Id}”→ 解锁并通知 {hNotes} 条（{hOpen}）");
        }

        private static string WeatherRowsFirst() =>
            ConfigSystem.Instance.Tables.TbWeather.DataList.First(w => w.Id != WeatherService.Clear).Id;

        // ─────────────────────────────── P 性能 ───────────────────────────────

        private static void CheckPerformance()
        {
            FreshCodex("perf");
            NewState(9381);
            MechanicCodex.OnEnemyEncountered("enemy_scout");
            var sw = Stopwatch.StartNew();
            const int n = 200000;
            for (int i = 0; i < n; i++)
            {
                MechanicCodex.OnEnemyEncountered("enemy_scout");
            }
            sw.Stop();
            Perf("战斗事件里的遭遇判定（已遇到过的快路径）", sw.Elapsed.TotalMilliseconds * 1000.0 / n, 0.5, "微秒 / 次；每次受伤 / 击毁事件一次");
            string title = EnemyDefs.NameOf("enemy_scout");
            UiTooltip.CodexIdOf(new TooltipContent { Title = title });
            sw.Restart();
            for (int i = 0; i < 20000; i++)
            {
                UiTooltip.CodexIdOf(new TooltipContent { Title = title });
            }
            sw.Stop();
            Perf("悬停提示按标题找条目", sw.Elapsed.TotalMilliseconds * 1000.0 / 20000, 5, "微秒 / 次；只在悬停 / 按图鉴键时");
            sw.Restart();
            MechanicCodex.Reload();
            int count = MechanicCodex.Entries.Count;
            sw.Stop();
            Perf($"重建全部条目（{count} 条，表 / 语言重载时）", sw.Elapsed.TotalMilliseconds, 50, "毫秒 / 次");
        }

        private static void Perf(string name, double value, double budget, string unit)
        {
            string line = $"{name}：{value:0.###}（预算 {budget}，{unit}）";
            PerfLines.Add(line);
            if (value > budget * 2)
            {
                Fail("性能超过预算 2 倍：" + line);
            }
            else if (value > budget)
            {
                Line("    ! 性能警告（未到 2 倍）：" + line);
            }
            else
            {
                _pass++;
                Line("    ✓ " + line);
            }
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgCodexSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
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
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        private static void Press(KeyCode key, bool alt = false)
        {
            InputRouter.DebugClearConsumedKeys();
            Keys.Down = key;
            Keys.Alt = alt;
            UiKitInputPump.ProcessLibraryKeys();
            Keys.Down = KeyCode.None;
            Keys.Alt = false;
            InputRouter.DebugClearConsumedKeys();
        }

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool Alt;
            public bool GetKey(KeyCode key) => key == Down || (Alt && key == KeyCode.LeftAlt);
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static string Short(string t)
        {
            string x = (t ?? string.Empty).Replace("\n", " / ");
            return x.Length > 90 ? x.Substring(0, 90) + "…" : x;
        }

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

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
